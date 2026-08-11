using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Tenancy;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.IntegrationTests.Tenancy;

/// <summary>Branch and organization management (Приложение D.0, TZ 39.1–39.3).</summary>
[Collection(PostgresCollection.Name)]
public sealed class BranchManagementTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private static readonly DateTimeOffset Seeded = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

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

    // -----------------------------------------------------------------
    // Visibility of the branch list (BRN-006)
    // -----------------------------------------------------------------

    [Fact]
    public async Task An_organization_admin_lists_every_branch()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<BranchDto>>(await client.GetAsync("/api/v1/branches"));

        page.TotalCount.ShouldBe(2);
        page.Items.Select(b => b.Id).ShouldBe([_headOfficeId, _khujandId], ignoreOrder: true);
    }

    /// <summary>
    /// 403, not an empty list: the roster of branches reveals the composition of the organization and
    /// lies outside branch isolation (<c>BRN-006</c>, <c>TEN-003</c>).
    /// </summary>
    [Theory]
    [InlineData("branch-admin-head@mentortaskflow.test")]
    [InlineData("lead-head@mentortaskflow.test")]
    [InlineData("mentor-head@mentortaskflow.test")]
    public async Task Nobody_else_may_list_branches(string email)
    {
        using var client = await SignInAsync(email);

        (await client.GetAsync("/api/v1/branches")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Reading one branch (BRN-007…BRN-009)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_branch_admin_reads_their_own_branch_and_no_other()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        (await client.GetAsync($"/api/v1/branches/{_headOfficeId}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A sibling branch is indistinguishable from one that does not exist (TEN-006).
        var foreign = await client.GetAsync($"/api/v1/branches/{_khujandId}");
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(foreign)).ShouldBe(ErrorCodes.ResourceNotFound);
    }

    /// <summary>
    /// A Lead gets the minimal projection: address and time zone serve no scenario of theirs, and the
    /// relevant time zone comes from CategorySettings (<c>BRN-009</c>).
    /// </summary>
    [Fact]
    public async Task A_lead_receives_only_the_summary_of_their_branch()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await client.GetAsync($"/api/v1/branches/{_headOfficeId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var branch = document.RootElement;

        branch.TryGetProperty("code", out _).ShouldBeTrue();
        branch.TryGetProperty("address", out _).ShouldBeFalse();
        branch.TryGetProperty("timeZoneId", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_branch_of_another_organization_answers_not_found()
    {
        Guid foreignBranchId;

        await using (var context = fixture.CreateContext(suppressTenantFilter: true))
        {
            var other = Organization.Provision("Other Academy", "other-academy", Seeded);
            context.Organizations.Add(other);

            var otherHeadOffice = Branch.CreateHeadOffice(other.Id, "Головной офис", "HQ", null, "Asia/Dushanbe", Seeded);
            context.Branches.Add(otherHeadOffice);

            await context.SaveChangesAsync();
            foreignBranchId = otherHeadOffice.Id;
        }

        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        (await client.GetAsync($"/api/v1/branches/{foreignBranchId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------
    // Creation (BRN-001, BRN-042, BRN-043)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Creating_a_branch_returns_201_and_never_makes_it_the_head_office()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/branches",
            new CreateBranchRequest("Филиал Бохтар", "BKH", "Бохтар, ул. Айни 5", "Asia/Dushanbe"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();

        var branch = await ReadAsync<BranchDto>(response);
        branch.IsHeadOffice.ShouldBeFalse();
        branch.IsActive.ShouldBeTrue();
        branch.OrganizationId.ShouldBe(_organizationId);
        branch.ConcurrencyToken.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary><c>API-031</c>: the flag is not in the schema, so strict deserialization refuses it.</summary>
    [Fact]
    public async Task A_request_carrying_is_head_office_is_refused()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await client.PostAsync("/api/v1/branches", JsonContent.Create(new
        {
            name = "Филиал Бохтар",
            code = "BKH",
            timeZoneId = "Asia/Dushanbe",
            isHeadOffice = true,
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    /// <summary><c>SEC-003</c>: scope is never accepted from the client.</summary>
    [Fact]
    public async Task A_request_carrying_organization_id_is_refused()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await client.PostAsync("/api/v1/branches", JsonContent.Create(new
        {
            name = "Филиал Бохтар",
            code = "BKH",
            timeZoneId = "Asia/Dushanbe",
            organizationId = Guid.CreateVersion7(),
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("HQ")]   // duplicate code
    [InlineData("NEW")]  // duplicate name
    public async Task A_duplicate_code_or_name_is_a_conflict(string code)
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var name = code == "HQ" ? "Совершенно новый филиал" : "Главный офис";

        var response = await client.PostAsJsonAsync("/api/v1/branches",
            new CreateBranchRequest(name, code, null, "Asia/Dushanbe"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.BranchAlreadyExists);
    }

    [Theory]
    [InlineData("Asia/Atlantis")]
    [InlineData("Central Asia Standard Time")]
    public async Task An_unknown_time_zone_is_refused(string timeZoneId)
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/branches",
            new CreateBranchRequest("Филиал Бохтар", "BKH", null, timeZoneId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Only_an_organization_admin_may_create_a_branch()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/branches",
            new CreateBranchRequest("Филиал Бохтар", "BKH", null, "Asia/Dushanbe"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Concurrency (11.6, API-026)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Updating_with_a_stale_token_is_a_conflict_and_returns_the_current_one()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var branch = await ReadAsync<BranchDto>(await client.GetAsync($"/api/v1/branches/{_khujandId}"));
        var staleToken = branch.ConcurrencyToken;

        var first = await client.PutAsJsonAsync($"/api/v1/branches/{_khujandId}",
            new UpdateBranchRequest("Филиал Худжанд-1", "KHJ", null, "Asia/Dushanbe", staleToken));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await client.PutAsJsonAsync($"/api/v1/branches/{_khujandId}",
            new UpdateBranchRequest("Филиал Худжанд-2", "KHJ", null, "Asia/Dushanbe", staleToken));

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(second)).ShouldBe(ErrorCodes.ConcurrencyConflict);

        // API-026: the current token travels with the conflict so the client can offer a reload
        // without a second round trip.
        using var document = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var currentToken = document.RootElement.GetProperty("currentConcurrencyToken").GetString();
        currentToken.ShouldNotBeNullOrWhiteSpace();
        currentToken.ShouldNotBe(staleToken);
    }

    [Fact]
    public async Task A_missing_concurrency_token_is_a_validation_failure()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await client.PutAsJsonAsync($"/api/v1/branches/{_khujandId}",
            new UpdateBranchRequest("Филиал Худжанд", "KHJ", null, "Asia/Dushanbe", string.Empty));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    // -----------------------------------------------------------------
    // Deactivation (BRN-030, BRN-034)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Deactivating_a_staffed_branch_needs_confirmation()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        var branch = await ReadAsync<BranchDto>(await client.GetAsync($"/api/v1/branches/{_khujandId}"));

        var refused = await client.PostAsJsonAsync($"/api/v1/branches/{_khujandId}/deactivate",
            new DeactivateBranchRequest(branch.ConcurrencyToken));

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(refused)).ShouldBe(ErrorCodes.BranchHasActiveUsers);

        var confirmed = await client.PostAsJsonAsync($"/api/v1/branches/{_khujandId}/deactivate",
            new DeactivateBranchRequest(branch.ConcurrencyToken, ConfirmActiveUsers: true));

        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<BranchDto>(confirmed)).IsActive.ShouldBeFalse();

        // BRN-030: the users are not deactivated with the branch.
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Users.CountAsync(u => u.BranchId == _khujandId && u.IsActive))
            .ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// <c>BRN-034</c>: an organization without a head office is an invalid state, so the flag must be
    /// passed on first.
    /// </summary>
    [Fact]
    public async Task The_head_office_cannot_be_deactivated_while_it_holds_the_flag()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        var branch = await ReadAsync<BranchDto>(await client.GetAsync($"/api/v1/branches/{_headOfficeId}"));

        var response = await client.PostAsJsonAsync($"/api/v1/branches/{_headOfficeId}/deactivate",
            new DeactivateBranchRequest(branch.ConcurrencyToken, ConfirmActiveUsers: true));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.HeadOfficeDeactivationForbidden);
    }

    [Fact]
    public async Task Deactivating_notifies_the_branch_and_is_audited()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        var branch = await ReadAsync<BranchDto>(await client.GetAsync($"/api/v1/branches/{_khujandId}"));

        await client.PostAsJsonAsync($"/api/v1/branches/{_khujandId}/deactivate",
            new DeactivateBranchRequest(branch.ConcurrencyToken, ConfirmActiveUsers: true));

        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var notifications = await context.NotificationOutbox
            .Where(n => n.EventType == NotificationEventTypes.BranchDeactivated)
            .ToListAsync();

        notifications.ShouldNotBeEmpty();
        notifications.ShouldAllBe(n => n.BranchId == _khujandId);

        // The scope prefix is what stops same-named events of two branches from colliding on the
        // unique index and silently suppressing one another (TEN-043).
        notifications.ShouldAllBe(n => n.DeduplicationKey.StartsWith(_organizationId.ToString("N")));

        var audit = await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.BranchDeactivate);

        // TEN-048: branch lifecycle is an organization-level action, invisible to a Branch Admin.
        audit.BranchId.ShouldBeNull();
        audit.EntityId.ShouldBe(_khujandId);
    }

    // -----------------------------------------------------------------
    // Head office transfer (TZ 39.3, TEST-TEN-032, TEST-TEN-033)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Transferring_the_head_office_moves_the_flag_atomically()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        var target = await ReadAsync<BranchDto>(await client.GetAsync($"/api/v1/branches/{_khujandId}"));

        var response = await client.PostAsJsonAsync($"/api/v1/branches/{_khujandId}/make-head-office",
            new BranchActionRequest(target.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<BranchDto>(response)).IsHeadOffice.ShouldBeTrue();

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var headOffices = await context.Branches.Where(b => b.IsHeadOffice).ToListAsync();

        headOffices.ShouldHaveSingleItem().Id.ShouldBe(_khujandId);
    }

    /// <summary>
    /// <c>TEST-TEN-033</c>: two competing transfers to different branches.
    /// </summary>
    /// <remarks>
    /// The assertion is deliberately <b>not</b> "exactly one 200". Two HTTP calls started together are
    /// not guaranteed to overlap: if the first finishes before the second begins, both succeed, and a
    /// pair of sequential transfers is correct behaviour rather than a defect. Asserting one success
    /// made this test flaky for exactly that reason.
    /// <para>
    /// What must hold on every interleaving is asserted instead: the database ends with exactly one
    /// head office, at least one caller wins, and a caller that loses is told 409 — never 500. The row
    /// lock on the organization narrows the window and <c>ux_branches_single_head_office</c> is the
    /// final guard (<c>BRN-021</c>, <c>BRN-046</c>).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Competing_transfers_leave_exactly_one_head_office()
    {
        Guid bokhtarId;

        using (var setup = await SignInAsync("organization-admin@mentortaskflow.test"))
        {
            var created = await ReadAsync<BranchDto>(await setup.PostAsJsonAsync("/api/v1/branches",
                new CreateBranchRequest("Филиал Бохтар", "BKH", null, "Asia/Dushanbe")));
            bokhtarId = created.Id;
        }

        using var clientA = await SignInAsync("organization-admin@mentortaskflow.test");
        using var clientB = await SignInAsync("organization-admin@mentortaskflow.test");

        var khujandToken = (await ReadAsync<BranchDto>(await clientA.GetAsync($"/api/v1/branches/{_khujandId}"))).ConcurrencyToken;
        var bokhtarToken = (await ReadAsync<BranchDto>(await clientB.GetAsync($"/api/v1/branches/{bokhtarId}"))).ConcurrencyToken;

        var responses = await Task.WhenAll(
            clientA.PostAsJsonAsync($"/api/v1/branches/{_khujandId}/make-head-office", new BranchActionRequest(khujandToken)),
            clientB.PostAsJsonAsync($"/api/v1/branches/{bokhtarId}/make-head-office", new BranchActionRequest(bokhtarToken)));

        responses.ShouldContain(r => r.StatusCode == HttpStatusCode.OK);

        // A caller that loses the race is told 409, never 500: somebody else won, which is a state
        // conflict the client resolves by reloading (BRN-046).
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            loser.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await ReadCodeAsync(loser)).ShouldBe(ErrorCodes.ConcurrencyConflict);
        }

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var headOffices = await context.Branches.Where(b => b.IsHeadOffice).ToListAsync();

        // The invariant that matters on every interleaving: exactly one head office, no partial state.
        headOffices.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_inactive_branch_cannot_become_the_head_office()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var branch = await ReadAsync<BranchDto>(await client.GetAsync($"/api/v1/branches/{_khujandId}"));
        var deactivated = await ReadAsync<BranchDto>(await client.PostAsJsonAsync(
            $"/api/v1/branches/{_khujandId}/deactivate",
            new DeactivateBranchRequest(branch.ConcurrencyToken, ConfirmActiveUsers: true)));

        var response = await client.PostAsJsonAsync($"/api/v1/branches/{_khujandId}/make-head-office",
            new BranchActionRequest(deactivated.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.HeadOfficeRequired);
    }

    // -----------------------------------------------------------------
    // Organization profile (ORG-003, ORG-004)
    // -----------------------------------------------------------------

    [Fact]
    public async Task An_organization_admin_sees_the_full_profile()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var organization = await ReadAsync<OrganizationDto>(await client.GetAsync("/api/v1/organization"));

        organization.Slug.ShouldBe("softclub-academy");
        organization.ConcurrencyToken.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Everyone else gets id and name only: slug and the service fields serve no scenario of theirs
    /// (<c>ORG-003</c>).
    /// </summary>
    [Theory]
    [InlineData("branch-admin-head@mentortaskflow.test")]
    [InlineData("lead-head@mentortaskflow.test")]
    [InlineData("mentor-head@mentortaskflow.test")]
    public async Task Everyone_else_sees_only_the_summary(string email)
    {
        using var client = await SignInAsync(email);

        var response = await client.GetAsync("/api/v1/organization");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        document.RootElement.TryGetProperty("name", out _).ShouldBeTrue();
        document.RootElement.TryGetProperty("slug", out _).ShouldBeFalse();
        document.RootElement.TryGetProperty("concurrencyToken", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Renaming_the_organization_leaves_the_slug_alone()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        var organization = await ReadAsync<OrganizationDto>(await client.GetAsync("/api/v1/organization"));

        var response = await client.PutAsJsonAsync("/api/v1/organization",
            new UpdateOrganizationRequest("SoftClub Group", organization.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await ReadAsync<OrganizationDto>(response);
        updated.Name.ShouldBe("SoftClub Group");
        updated.Slug.ShouldBe("softclub-academy");
    }

    [Fact]
    public async Task Only_an_organization_admin_may_rename_the_organization()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await client.PutAsJsonAsync("/api/v1/organization",
            new UpdateOrganizationRequest("Захвачено", "irrelevant"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ValidPassword));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await ReadAsync<LoginResponse>(response);
        client.DefaultRequestHeaders.Authorization = new("Bearer", body.AccessToken);

        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<T>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
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
        var khujandCategory = Category.Create(organization.Id, khujand.Id, "C#", null, Seeded);
        context.Categories.AddRange(headCategory, khujandCategory);
        context.CategorySettings.AddRange(
            CategorySettings.CreateDefault(headCategory, headOffice.TimeZoneId, Seeded),
            CategorySettings.CreateDefault(khujandCategory, khujand.TimeZoneId, Seeded));

        var users = new List<User>
        {
            User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded),
            User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded),
            User.CreateLead(organization.Id, headOffice.Id, headCategory.Id, "Лид", "lead-head@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, khujand.Id, khujandCategory.Id, "Ментор Худжанда", "mentor-khujand@mentortaskflow.test", Seeded),
        };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        _organizationId = organization.Id;
        _headOfficeId = headOffice.Id;
        _khujandId = khujand.Id;
    }
}
