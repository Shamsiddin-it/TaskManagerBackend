using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Admin;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MentorTaskFlow.IntegrationTests.Auditing;

/// <summary>
/// <c>TEST-TEN-018</c>: a Branch Admin sees only their own branch's records, and never the
/// organization-level ones.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuditLogVisibilityTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _organizationId;
    private Guid _headOfficeId;
    private Guid _khujandId;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await SeedAsync();

        _factory = new MentorTaskFlowApiFactory { ConnectionStringOverride = fixture.ConnectionString };
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task An_organization_admin_sees_both_branches_and_the_organization_level_records()
    {
        var page = await ReadAuditLogAsync("organization-admin@mentortaskflow.test");

        page.Items.ShouldContain(e => e.BranchId == _headOfficeId);
        page.Items.ShouldContain(e => e.BranchId == _khujandId);
        page.Items.ShouldContain(e => e.BranchId == null);
    }

    /// <summary>
    /// The organization-level rows name other branches and expose the composition of the
    /// organization, so a Branch Admin must not see them (<c>TEN-049</c>).
    /// </summary>
    [Fact]
    public async Task A_branch_admin_sees_only_their_own_branch()
    {
        var page = await ReadAuditLogAsync("branch-admin-head@mentortaskflow.test");

        page.Items.ShouldNotBeEmpty();
        page.Items.ShouldAllBe(e => e.BranchId == _headOfficeId);
        page.Items.ShouldNotContain(e => e.BranchId == null);
        page.Items.ShouldNotContain(e => e.BranchId == _khujandId);
    }

    /// <summary>
    /// <c>TEST-TEN-003</c>: the counter is computed under the same predicate, so it cannot disclose
    /// the existence of rows the caller may not read.
    /// </summary>
    [Fact]
    public async Task The_total_count_matches_what_the_caller_may_read()
    {
        var organizationAdmin = await ReadAuditLogAsync("organization-admin@mentortaskflow.test");
        var branchAdmin = await ReadAuditLogAsync("branch-admin-head@mentortaskflow.test");

        branchAdmin.TotalCount.ShouldBeLessThan(organizationAdmin.TotalCount);
    }

    [Theory]
    [InlineData("lead-head@mentortaskflow.test")]
    [InlineData("mentor-head@mentortaskflow.test")]
    public async Task Lead_and_mentor_have_no_access_at_all(string email)
    {
        using var client = _factory.CreateClient();
        var token = await SignInAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync("/api/v1/admin/audit-log");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        using var client = _factory.CreateClient();

        (await client.GetAsync("/api/v1/admin/audit-log")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary><c>AUD-023</c>: reading the log is itself audited, naming the scope applied.</summary>
    [Fact]
    public async Task Reading_the_log_is_recorded_with_the_applied_scope()
    {
        await ReadAuditLogAsync("branch-admin-head@mentortaskflow.test");

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var read = await context.AuditLogs
            .Where(log => log.Action == AuditActions.AuditRead)
            .OrderByDescending(log => log.OccurredAt)
            .FirstAsync();

        read.ActorAdminScope.ShouldBe(AdminScope.Branch);
        read.BranchId.ShouldBe(_headOfficeId);

        var metadata = read.Metadata!.RootElement;
        metadata.GetProperty("branchFilter").GetString().ShouldBe(_headOfficeId.ToString());
        metadata.GetProperty("adminScope").GetString().ShouldBe(nameof(AdminScope.Branch));
    }

    /// <summary>
    /// <c>AUD-001</c>: append-only is a database guarantee. The application role has no UPDATE or
    /// DELETE, so a defect that tries to rewrite history is refused even having reached the database.
    /// </summary>
    [Fact]
    public async Task The_application_role_cannot_rewrite_history()
    {
        await using var connection = await fixture.OpenRawConnectionAsync();

        await using (var grant = connection.CreateCommand())
        {
            // The Testcontainers image has only the default superuser, so the role the production
            // migration targets is created here to make the revoke observable.
            grant.CommandText = """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mentortaskflow_app_probe') THEN
                        CREATE ROLE mentortaskflow_app_probe;
                    END IF;
                    GRANT SELECT, INSERT, UPDATE, DELETE ON audit_logs TO mentortaskflow_app_probe;
                    REVOKE UPDATE, DELETE ON audit_logs FROM mentortaskflow_app_probe;
                END
                $$;
                """;
            await grant.ExecuteNonQueryAsync();
        }

        await using var probe = connection.CreateCommand();
        probe.CommandText = """
            SET LOCAL ROLE mentortaskflow_app_probe;
            UPDATE audit_logs SET action = 'tampered';
            """;

        var exception = await Should.ThrowAsync<PostgresException>(probe.ExecuteNonQueryAsync());
        exception.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<PagedResult<AuditLogEntryDto>> ReadAuditLogAsync(string email)
    {
        using var client = _factory.CreateClient();
        var token = await SignInAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync("/api/v1/admin/audit-log?pageSize=100");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return JsonSerializer.Deserialize<PagedResult<AuditLogEntryDto>>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<string> SignInAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ValidPassword));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<LoginResponse>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        return body.AccessToken;
    }

    private async Task SeedAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var passwordHash = new Pbkdf2PasswordHasher().Hash(ValidPassword);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Seeded);
        var khujand = Branch.Create(organization.Id, "Филиал Худжанд", "KHJ", null, "Asia/Dushanbe", Seeded);
        context.Branches.AddRange(headOffice, khujand);

        var headCategory = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        context.Categories.Add(headCategory);
        context.CategorySettings.Add(CategorySettings.CreateDefault(headCategory, headOffice.TimeZoneId, Seeded));

        var users = new List<User>
        {
            User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded),
            User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded),
            User.CreateLead(organization.Id, headOffice.Id, headCategory.Id, "Лид", "lead-head@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded),
        };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        var actor = users[0];

        // One record per visibility class: head office, Khujand, and organization-level.
        context.AuditLogs.AddRange(
            AuditLog.Record(
                AuditActions.CategoryCreate, nameof(Category), organization.Id, headOffice.Id, headCategory.Id,
                headCategory.Id, AuditActorType.User, actor.Id, UserRole.Admin, AdminScope.Organization,
                AuditResult.Success, Guid.CreateVersion7(), Seeded),
            AuditLog.Record(
                AuditActions.UserDeactivate, nameof(User), organization.Id, khujand.Id, null,
                Guid.CreateVersion7(), AuditActorType.User, actor.Id, UserRole.Admin, AdminScope.Organization,
                AuditResult.Success, Guid.CreateVersion7(), Seeded),
            AuditLog.Record(
                AuditActions.BranchCreate, nameof(Branch), organization.Id, null, null,
                khujand.Id, AuditActorType.User, actor.Id, UserRole.Admin, AdminScope.Organization,
                AuditResult.Success, Guid.CreateVersion7(), Seeded));

        await context.SaveChangesAsync();

        _organizationId = organization.Id;
        _headOfficeId = headOffice.Id;
        _khujandId = khujand.Id;
    }
}
