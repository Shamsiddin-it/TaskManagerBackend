using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Notifications;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.IntegrationTests.Notifications;

/// <summary>The outbox journal and its scope limits (TZ 18.8, Приложение D.7).</summary>
[Collection(PostgresCollection.Name)]
public sealed class NotificationAdminTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _organizationId;
    private Guid _headOfficeId;
    private Guid _khujandId;
    private Guid _headMentorId;
    private Guid _khujandMentorId;
    private Guid _organizationAdminId;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        await SeedAsync();

        _factory = new MentorTaskFlowApiFactory { ConnectionStringOverride = postgres.ConnectionString };
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary><c>TEN-046</c>: a Branch Admin sees their own branch and nothing else.</summary>
    [Fact]
    public async Task A_branch_admin_sees_only_their_own_branch()
    {
        await PlantAsync(_headOfficeId, _headMentorId);
        await PlantAsync(_khujandId, _khujandMentorId);

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<NotificationDto>>(await admin.GetAsync("/api/v1/admin/notifications"));

        page.Items.ShouldAllBe(n => n.BranchId == _headOfficeId);

        // The counter runs under the same predicate: one that saw more would disclose the volume of
        // another branch's traffic.
        page.TotalCount.ShouldBe(1);
    }

    /// <summary>
    /// Organization-level rows are invisible to a Branch Admin: they concern branches other than
    /// theirs and would disclose the composition of the organization (<c>TEN-049</c>).
    /// </summary>
    [Fact]
    public async Task A_branch_admin_does_not_see_organization_level_rows()
    {
        await PlantOrganizationLevelAsync();

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<NotificationDto>>(await admin.GetAsync("/api/v1/admin/notifications"));

        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_organization_admin_sees_every_branch_and_the_organization_level_rows()
    {
        await PlantAsync(_headOfficeId, _headMentorId);
        await PlantAsync(_khujandId, _khujandMentorId);
        await PlantOrganizationLevelAsync();

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<NotificationDto>>(await admin.GetAsync("/api/v1/admin/notifications"));

        page.TotalCount.ShouldBe(3);
        page.Items.ShouldContain(n => n.BranchId == null);
    }

    [Fact]
    public async Task An_organization_admin_can_narrow_to_one_branch()
    {
        await PlantAsync(_headOfficeId, _headMentorId);
        await PlantAsync(_khujandId, _khujandMentorId);

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");
        admin.DefaultRequestHeaders.Add("X-MTF-Branch-Id", _khujandId.ToString());

        var page = await ReadAsync<PagedResult<NotificationDto>>(await admin.GetAsync("/api/v1/admin/notifications"));

        page.Items.ShouldHaveSingleItem().BranchId.ShouldBe(_khujandId);
    }

    [Fact]
    public async Task The_journal_can_be_filtered_by_status()
    {
        await PlantAsync(_headOfficeId, _headMentorId);
        await PlantAsync(_headOfficeId, _headMentorId, discriminator: "2", deadLetter: true);

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<NotificationDto>>(
            await admin.GetAsync("/api/v1/admin/notifications?status=DeadLetter"));

        page.Items.ShouldHaveSingleItem().Status.ShouldBe(nameof(NotificationStatus.DeadLetter));
    }

    /// <summary>The payload may name a task or a person; the journal is for operating the queue.</summary>
    [Fact]
    public async Task The_journal_does_not_expose_the_payload()
    {
        await PlantAsync(_headOfficeId, _headMentorId);

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var body = await (await admin.GetAsync("/api/v1/admin/notifications")).Content.ReadAsStringAsync();

        body.ShouldNotContain("Секретная задача");
    }

    [Fact]
    public async Task A_mentor_has_no_access_to_the_journal()
    {
        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        (await mentor.GetAsync("/api/v1/admin/notifications")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Manual retry (NTF-014, TEN-047)
    // -----------------------------------------------------------------

    [Fact]
    public async Task An_admin_returns_a_dead_lettered_row_to_the_queue()
    {
        var id = await PlantAsync(_headOfficeId, _headMentorId, deadLetter: true);

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await admin.PostAsync($"/api/v1/admin/notifications/{id}/retry", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var retried = await ReadAsync<NotificationDto>(response);
        retried.Status.ShouldBe(nameof(NotificationStatus.Pending));
        retried.Attempts.ShouldBe(0);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        (await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.NotificationRetry)).EntityId.ShouldBe(id);
    }

    /// <summary>A row already on its way must not be reset — that would deliver the message twice.</summary>
    [Fact]
    public async Task A_pending_row_cannot_be_retried()
    {
        var id = await PlantAsync(_headOfficeId, _headMentorId);

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        (await admin.PostAsync($"/api/v1/admin/notifications/{id}/retry", null))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// <c>TEN-047</c>: a row of another branch answers 404, not 403 — its existence is not theirs to
    /// learn.
    /// </summary>
    [Fact]
    public async Task A_branch_admin_cannot_retry_another_branchs_row()
    {
        var id = await PlantAsync(_khujandId, _khujandMentorId, deadLetter: true);

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        (await admin.PostAsync($"/api/v1/admin/notifications/{id}/retry", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_branch_admin_cannot_retry_an_organization_level_row()
    {
        var id = await PlantOrganizationLevelAsync(deadLetter: true);

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        (await admin.PostAsync($"/api/v1/admin/notifications/{id}/retry", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    private Task<Guid> PlantAsync(
        Guid branchId,
        Guid recipientId,
        string discriminator = "1",
        bool deadLetter = false) =>
        PlantCoreAsync(
            NotificationEventTypes.AssignmentAssigned,
            recipientId,
            branchId,
            discriminator,
            deadLetter);

    private Task<Guid> PlantOrganizationLevelAsync(bool deadLetter = false) =>
        PlantCoreAsync(
            NotificationEventTypes.BranchWithoutAdmin,
            _organizationAdminId,
            branchId: null,
            "1",
            deadLetter);

    private async Task<Guid> PlantCoreAsync(
        string eventType,
        Guid recipientId,
        Guid? branchId,
        string discriminator,
        bool deadLetter)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var row = NotificationOutbox.Enqueue(
            recipientId,
            _organizationId,
            branchId,
            categoryId: null,
            NotificationChannel.Email,
            eventType,
            JsonSerializer.SerializeToDocument(new { assignmentTitle = "Секретная задача" }),
            DeduplicationKey.Build(
                _organizationId,
                branchId,
                eventType,
                Guid.CreateVersion7(),
                NotificationChannel.Email,
                discriminator),
            Seeded);

        if (deadLetter)
        {
            row.Capture("worker-test", Seeded);
            row.SendToDeadLetter("SMTP 5.1.1 unknown mailbox", Seeded);
        }

        context.NotificationOutbox.Add(row);
        await context.SaveChangesAsync();

        return row.Id;
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ValidPassword));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Authorization =
            new("Bearer", (await ReadAsync<LoginResponse>(response)).AccessToken);

        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<T>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private async Task SeedAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var passwordHash = new Pbkdf2PasswordHasher().Hash(ValidPassword);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Seeded);
        var khujand = Branch.Create(organization.Id, "Филиал Худжанд", "KHJ", null, "Asia/Dushanbe", Seeded);
        context.Branches.AddRange(headOffice, khujand);

        var headCategory = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        var khujandCategory = Category.Create(organization.Id, khujand.Id, "C#", null, Seeded);
        context.Categories.AddRange(headCategory, khujandCategory);

        context.CategorySettings.AddRange(
            CategorySettings.CreateDefault(headCategory, headOffice.TimeZoneId, Seeded),
            CategorySettings.CreateDefault(khujandCategory, khujand.TimeZoneId, Seeded));

        var admin = User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded);
        var branchAdmin = User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded);
        var mentor = User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded);
        var khujandMentor = User.CreateMentor(organization.Id, khujand.Id, khujandCategory.Id, "Ментор Худжанда", "mentor-khujand@mentortaskflow.test", Seeded);

        var users = new List<User> { admin, branchAdmin, mentor, khujandMentor };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        _organizationId = organization.Id;
        _headOfficeId = headOffice.Id;
        _khujandId = khujand.Id;
        _headMentorId = mentor.Id;
        _khujandMentorId = khujandMentor.Id;
        _organizationAdminId = admin.Id;
    }
}
