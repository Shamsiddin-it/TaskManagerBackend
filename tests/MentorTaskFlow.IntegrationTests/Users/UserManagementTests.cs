using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Users;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.IntegrationTests.Users;

/// <summary>User administration (Приложение D.2, TZ 15.1, 39.5).</summary>
[Collection(PostgresCollection.Name)]
public sealed class UserManagementTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string BranchHeader = "X-MTF-Branch-Id";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _headOfficeId;
    private Guid _khujandId;
    private Guid _headCategoryId;
    private Guid _khujandCategoryId;
    private Guid _headLeadId;
    private Guid _headMentorId;
    private Guid _khujandMentorId;
    private Guid _headBranchAdminId;

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
    // The creation matrix (USER-031)
    // -----------------------------------------------------------------

    /// <summary>
    /// The decision the TZ singles out: a Branch Admin cannot create another Branch Admin. Letting a
    /// branch's contour reproduce itself would take the composition of administrators out of the
    /// organization's control and create an escalation path invisible at organization level.
    /// </summary>
    [Fact]
    public async Task A_branch_admin_cannot_create_another_branch_admin()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Новый админ", "new-branch-admin@mentortaskflow.test", "Admin", "Branch"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // USER-031: the attempt is recorded — this is exactly what a review needs to see.
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var audit = await context.AuditLogs
            .Where(a => a.Action == AuditActions.UserCreate && a.Result == AuditResult.Failure)
            .SingleAsync();

        audit.FailureReason.ShouldBe(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task A_branch_admin_cannot_create_an_organization_admin()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Новый админ", "new-org-admin@mentortaskflow.test", "Admin", "Organization"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Lead")]
    [InlineData("Mentor")]
    public async Task A_branch_admin_creates_leads_and_mentors_in_their_own_branch(string role)
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest($"Новый {role}", $"new-{role.ToLowerInvariant()}@mentortaskflow.test", role,
                CategoryId: _headCategoryId));

        // A second active Lead is impossible in a category that already has one, so the seeded Lead is
        // deactivated first by the test that needs it; here Mentor succeeds and Lead conflicts.
        if (role == "Mentor")
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Created);

            var user = await ReadAsync<UserDto>(response);
            user.BranchId.ShouldBe(_headOfficeId);
            user.CategoryId.ShouldBe(_headCategoryId);

            // AUTH-019: no password is generated. The account exists, occupies its email, and waits
            // for its owner to set one through a one-time link.
            user.HasPassword.ShouldBeFalse();
        }
        else
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ActiveLeadAlreadyExists);
        }
    }

    /// <summary><c>USER-002</c>: a Lead creates only mentors, only in their own category.</summary>
    [Fact]
    public async Task A_lead_creates_a_mentor_in_their_own_category()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Новый ментор", "new-mentor@mentortaskflow.test", "Mentor"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var user = await ReadAsync<UserDto>(response);
        user.CategoryId.ShouldBe(_headCategoryId);
        user.BranchId.ShouldBe(_headOfficeId);
    }

    [Theory]
    [InlineData("Lead")]
    [InlineData("Admin")]
    public async Task A_lead_cannot_create_anything_but_a_mentor(string role)
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Кто-то", "someone@mentortaskflow.test", role,
                AdminScope: role == "Admin" ? "Branch" : null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// <c>USER-032</c>: a Lead cannot name another category. Their own is taken from the claim, and
    /// the field in the body is ignored rather than trusted.
    /// </summary>
    [Fact]
    public async Task A_lead_cannot_place_a_mentor_in_another_category()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Новый ментор", "new-mentor@mentortaskflow.test", "Mentor",
                CategoryId: _khujandCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReadAsync<UserDto>(response)).CategoryId.ShouldBe(_headCategoryId);
    }

    [Fact]
    public async Task A_mentor_cannot_create_anybody()
    {
        using var client = await SignInAsync("mentor-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Кто-то", "someone@mentortaskflow.test", "Mentor"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Scope on creation (USER-032, TEN-033)
    // -----------------------------------------------------------------

    [Fact]
    public async Task An_organization_admin_must_choose_a_branch_for_a_branch_scoped_user()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Новый ментор", "new-mentor@mentortaskflow.test", "Mentor",
                CategoryId: _headCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.BranchContextRequired);
    }

    /// <summary>
    /// An Organization Admin belongs to no branch, so the header is meaningless for that contour and
    /// its presence is refused rather than ignored (<c>USER-032</c>).
    /// </summary>
    [Fact]
    public async Task Creating_an_organization_admin_refuses_a_chosen_branch()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        client.DefaultRequestHeaders.Add(BranchHeader, _headOfficeId.ToString());

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Второй админ", "second-org-admin@mentortaskflow.test", "Admin", "Organization"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_organization_admin_creates_a_second_organization_admin()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Второй админ", "second-org-admin@mentortaskflow.test", "Admin", "Organization"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var user = await ReadAsync<UserDto>(response);
        user.AdminScope.ShouldBe("Organization");
        user.BranchId.ShouldBeNull();
        user.CategoryId.ShouldBeNull();

        // TEN-016: several active Organization Admins are permitted deliberately — a single one is a
        // single point of failure when access is lost.
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Users.CountAsync(u => u.AdminScope == AdminScope.Organization)).ShouldBe(2);
    }

    /// <summary>
    /// <c>TEN-024</c>: a category of another branch is refused. The composite FK
    /// <c>fk_users_category_scope</c> forbids the same combination in the database.
    /// </summary>
    [Fact]
    public async Task A_category_from_another_branch_is_a_cross_scope_reference()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        client.DefaultRequestHeaders.Add(BranchHeader, _headOfficeId.ToString());

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Новый ментор", "new-mentor@mentortaskflow.test", "Mentor",
                CategoryId: _khujandCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.CrossScopeReference);
    }

    [Fact]
    public async Task A_duplicate_email_is_a_conflict()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Дубль", "mentor-head@mentortaskflow.test", "Mentor", CategoryId: _headCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ResourceAlreadyExists);
    }

    /// <summary><c>SEC-003</c>: scope is never accepted from the body.</summary>
    [Fact]
    public async Task A_request_carrying_branch_id_is_refused()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await client.PostAsync("/api/v1/users", JsonContent.Create(new
        {
            fullName = "Кто-то",
            email = "someone@mentortaskflow.test",
            role = "Mentor",
            categoryId = _headCategoryId,
            branchId = Guid.CreateVersion7(),
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    // -----------------------------------------------------------------
    // Invitation (USER-034)
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-037</c>: the invitation names the organization, the branch and the category, and
    /// contains no UUID. Internal identifiers are of no use to the recipient and of considerable use
    /// to anyone who intercepts the message.
    /// </summary>
    [Fact]
    public async Task The_invitation_carries_names_and_no_identifiers()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        client.DefaultRequestHeaders.Add(BranchHeader, _khujandId.ToString());

        var created = await ReadAsync<UserDto>(await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Новый лид", "new-lead@mentortaskflow.test", "Lead",
                CategoryId: _khujandCategoryId)));

        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var invitation = await context.NotificationOutbox
            .Where(n => n.EventType == NotificationEventTypes.UserInvitation && n.UserId == created.Id)
            .SingleAsync();

        var payload = invitation.Payload.RootElement;
        payload.GetProperty("organizationName").GetString().ShouldBe("SoftClub Academy");
        payload.GetProperty("branchName").GetString().ShouldBe("Филиал Худжанд");
        payload.GetProperty("categoryName").GetString().ShouldBe("C#");

        var raw = invitation.Payload.RootElement.GetRawText();
        raw.ShouldNotContain(created.Id.ToString());
        raw.ShouldNotContain(_khujandId.ToString());
        raw.ShouldNotContain(_khujandCategoryId.ToString());

        // A set-password token was issued, so the invitation is actionable (AUTH-020).
        (await context.UserSecurityTokens.CountAsync(
            t => t.UserId == created.Id && t.Purpose == SecurityTokenPurpose.SetPassword)).ShouldBe(1);
    }

    /// <summary><c>AUTH-017</c>: exactly one live link — reissuing retires the previous one.</summary>
    [Fact]
    public async Task Resending_an_invitation_invalidates_the_previous_link()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var created = await ReadAsync<UserDto>(await client.PostAsJsonAsync("/api/v1/users",
            new CreateUserRequest("Новый ментор", "new-mentor@mentortaskflow.test", "Mentor")));

        var response = await client.PostAsJsonAsync($"/api/v1/users/{created.Id}/resend-invitation", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var tokens = await context.UserSecurityTokens
            .Where(t => t.UserId == created.Id)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();

        tokens.Count.ShouldBe(2);
        tokens[0].InvalidatedAt.ShouldNotBeNull();
        tokens[1].InvalidatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_lead_cannot_resend_to_somebody_elses_mentor()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync($"/api/v1/users/{_khujandMentorId}/resend-invitation", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------
    // Visibility (USER-010)
    // -----------------------------------------------------------------

    [Fact]
    public async Task An_organization_admin_sees_the_whole_organization()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<UserDto>>(await client.GetAsync("/api/v1/users?pageSize=100"));

        page.Items.Select(u => u.BranchId).ShouldContain(_khujandId);
        page.Items.Select(u => u.BranchId).ShouldContain(_headOfficeId);
    }

    [Fact]
    public async Task A_branch_admin_sees_only_their_own_branch()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<UserDto>>(await client.GetAsync("/api/v1/users?pageSize=100"));

        page.Items.ShouldAllBe(u => u.BranchId == _headOfficeId);
        page.Items.ShouldNotContain(u => u.AdminScope == "Organization");
    }

    [Fact]
    public async Task A_lead_sees_only_their_own_category()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<UserDto>>(await client.GetAsync("/api/v1/users?pageSize=100"));

        page.Items.ShouldAllBe(u => u.CategoryId == _headCategoryId);
    }

    /// <summary>
    /// <c>USER-010</c>: a Mentor has no access to the user list at all — they may not see the personal
    /// data of other mentors, not even in their own category.
    /// </summary>
    [Fact]
    public async Task A_mentor_has_no_access_to_the_list()
    {
        using var client = await SignInAsync("mentor-head@mentortaskflow.test");

        (await client.GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_user_of_another_branch_answers_not_found()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await client.GetAsync($"/api/v1/users/{_khujandMentorId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ResourceNotFound);
    }

    // -----------------------------------------------------------------
    // Deactivation (USER-004, USER-005, USER-036)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Deactivation_ends_every_session_immediately()
    {
        // The mentor signs in first, so there is a live session to end.
        using var mentorClient = _factory.CreateClient();
        var mentorLogin = await mentorClient.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("mentor-head@mentortaskflow.test", ValidPassword));
        var mentorToken = (await ReadAsync<LoginResponse>(mentorLogin)).AccessToken;

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var user = await ReadAsync<UserDto>(await admin.GetAsync($"/api/v1/users/{_headMentorId}"));

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_headMentorId}/deactivate",
            new UserActionRequest(user.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<UserDto>(response)).IsActive.ShouldBeFalse();

        // AUTH-034: the access token dies with the account.
        mentorClient.DefaultRequestHeaders.Authorization = new("Bearer", mentorToken);
        var afterDeactivation = await mentorClient.GetAsync("/api/v1/auth/me");
        afterDeactivation.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var tokens = await context.RefreshTokens.Where(t => t.UserId == _headMentorId).ToListAsync();
        tokens.ShouldAllBe(t => t.ReasonRevoked == RefreshTokenRevocationReason.Deactivated);

        // A deactivated account must not be able to set a password and undo the deactivation from
        // outside (USER-004).
        (await context.UserSecurityTokens.CountAsync(
            t => t.UserId == _headMentorId && t.UsedAt == null && t.InvalidatedAt == null)).ShouldBe(0);
    }

    /// <summary>
    /// <c>USER-005</c>: deactivating the last Lead is allowed and raises a notification. Refusing
    /// would strand a category whose only Lead has left.
    /// </summary>
    [Fact]
    public async Task Deactivating_the_last_lead_is_allowed_and_notifies_the_admins()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var lead = await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/users/{_headLeadId}"));

        var response = await client.PostAsJsonAsync($"/api/v1/users/{_headLeadId}/deactivate",
            new UserActionRequest(lead.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var notifications = await context.NotificationOutbox
            .Where(n => n.EventType == NotificationEventTypes.CategoryWithoutLead)
            .ToListAsync();

        notifications.ShouldNotBeEmpty();
        notifications.ShouldAllBe(n => n.BranchId == _headOfficeId);
    }

    /// <summary>
    /// <c>USER-036</c> and <c>TEN-042</c>: a branch left without an administrator is reported to the
    /// organization administrators, and the row carries no branch because the event concerns the
    /// organization.
    /// </summary>
    [Fact]
    public async Task Deactivating_the_last_branch_admin_notifies_the_organization()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        var admin = await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/users/{_headBranchAdminId}"));

        var response = await client.PostAsJsonAsync($"/api/v1/users/{_headBranchAdminId}/deactivate",
            new UserActionRequest(admin.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var notification = await context.NotificationOutbox
            .Where(n => n.EventType == NotificationEventTypes.BranchWithoutAdmin)
            .SingleAsync();

        notification.BranchId.ShouldBeNull();
    }

    [Fact]
    public async Task A_deactivated_lead_can_be_reactivated_when_the_seat_is_free()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var lead = await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/users/{_headLeadId}"));
        var deactivated = await ReadAsync<UserDto>(await client.PostAsJsonAsync(
            $"/api/v1/users/{_headLeadId}/deactivate", new UserActionRequest(lead.ConcurrencyToken)));

        var reactivated = await client.PostAsJsonAsync($"/api/v1/users/{_headLeadId}/activate",
            new UserActionRequest(deactivated.ConcurrencyToken));

        reactivated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<UserDto>(reactivated)).IsActive.ShouldBeTrue();
    }

    // -----------------------------------------------------------------
    // Role and contour (USER-008, USER-033)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_branch_admin_cannot_promote_anybody_to_admin()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var mentor = await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/users/{_headMentorId}"));

        var response = await client.PostAsJsonAsync($"/api/v1/users/{_headMentorId}/change-role",
            new ChangeRoleRequest("Admin", "Повышение по итогам аттестации", mentor.ConcurrencyToken, "Branch"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_organization_admin_moves_an_admin_between_contours()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        var branchAdmin = await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/users/{_headBranchAdminId}"));

        var response = await client.PostAsJsonAsync($"/api/v1/users/{_headBranchAdminId}/change-role",
            new ChangeRoleRequest("Admin", "Перевод в контур организации", branchAdmin.ConcurrencyToken, "Organization"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await ReadAsync<UserDto>(response);
        updated.AdminScope.ShouldBe("Organization");

        // USER-033: moving into the organization contour clears the branch — it belongs to none.
        updated.BranchId.ShouldBeNull();

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var audit = await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.UserChangeAdminScope);
        audit.Metadata!.RootElement.GetProperty("reason").GetString().ShouldBe("Перевод в контур организации");

        // Any change of access level invalidates issued tokens at once (AUTH-034).
        var user = await context.Users.SingleAsync(u => u.Id == _headBranchAdminId);
        user.TokenVersion.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_reason_is_mandatory_for_a_role_change()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        var mentor = await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/users/{_headMentorId}"));

        var response = await client.PostAsJsonAsync($"/api/v1/users/{_headMentorId}/change-role",
            new ChangeRoleRequest("Lead", "нет", mentor.ConcurrencyToken, CategoryId: _headCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    /// <summary><c>USER-008</c>: promoting a mentor while the category already has a Lead is refused.</summary>
    [Fact]
    public async Task Promoting_a_mentor_while_a_lead_exists_is_a_conflict()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        var mentor = await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/users/{_headMentorId}"));

        var response = await client.PostAsJsonAsync($"/api/v1/users/{_headMentorId}/change-role",
            new ChangeRoleRequest("Lead", "Повышение по итогам аттестации", mentor.ConcurrencyToken,
                CategoryId: _headCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ActiveLeadAlreadyExists);
    }

    // -----------------------------------------------------------------
    // Patch (API-009)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Patching_changes_the_name_only()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var mentor = await ReadAsync<UserDto>(await client.GetAsync($"/api/v1/users/{_headMentorId}"));

        var response = await client.PatchAsJsonAsync($"/api/v1/users/{_headMentorId}",
            new PatchUserRequest("Ментор Обновлённый", mentor.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await ReadAsync<UserDto>(response);
        updated.FullName.ShouldBe("Ментор Обновлённый");
        updated.Email.ShouldBe(mentor.Email);
        updated.Role.ShouldBe(mentor.Role);
    }

    [Fact]
    public async Task A_lead_cannot_patch_a_user()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await client.PatchAsJsonAsync($"/api/v1/users/{_headMentorId}",
            new PatchUserRequest("Переименован", "irrelevant"));

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

        client.DefaultRequestHeaders.Authorization =
            new("Bearer", (await ReadAsync<LoginResponse>(response)).AccessToken);

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

        var organizationAdmin = User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded);
        var headBranchAdmin = User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded);
        var headLead = User.CreateLead(organization.Id, headOffice.Id, headCategory.Id, "Лид", "lead-head@mentortaskflow.test", Seeded);
        var headMentor = User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded);
        var khujandMentor = User.CreateMentor(organization.Id, khujand.Id, khujandCategory.Id, "Ментор Худжанда", "mentor-khujand@mentortaskflow.test", Seeded);

        var users = new List<User> { organizationAdmin, headBranchAdmin, headLead, headMentor, khujandMentor };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        _headOfficeId = headOffice.Id;
        _khujandId = khujand.Id;
        _headCategoryId = headCategory.Id;
        _khujandCategoryId = khujandCategory.Id;
        _headLeadId = headLead.Id;
        _headMentorId = headMentor.Id;
        _khujandMentorId = khujandMentor.Id;
        _headBranchAdminId = headBranchAdmin.Id;
    }
}
