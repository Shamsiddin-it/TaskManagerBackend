using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Assignments;
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

/// <summary>Category and branch transfers (TZ 15.2, 39.6, 39.7).</summary>
[Collection(PostgresCollection.Name)]
public sealed class UserTransferTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string Reason = "Перевод по заявлению сотрудника";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _headOfficeId;
    private Guid _khujandId;
    private Guid _sharpId;
    private Guid _pythonId;
    private Guid _archivedId;
    private Guid _khujandCategoryId;
    private Guid _mentorId;
    private Guid _leadId;

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
    // Category transfer (TZ 15.2)
    // -----------------------------------------------------------------

    /// <summary><c>USER-015</c>: one transaction — the field, the history row, the sessions, the audit.</summary>
    [Fact]
    public async Task A_category_transfer_moves_the_user_and_records_it()
    {
        await SeedRefreshTokenAsync(_mentorId);

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-category",
            new ChangeCategoryRequest(_pythonId, Reason, await TokenOfAsync(admin, _mentorId)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<UserDto>(response)).CategoryId.ShouldBe(_pythonId);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var history = await context.UserCategoryHistory.SingleAsync(h => h.UserId == _mentorId);
        history.PreviousCategoryId.ShouldBe(_sharpId);
        history.NewCategoryId.ShouldBe(_pythonId);
        history.BranchId.ShouldBe(_headOfficeId);
        history.Reason.ShouldBe(Reason);

        // AUTH-034: the transfer is worthless if the old session keeps its old category claim.
        var user = await context.Users.SingleAsync(u => u.Id == _mentorId);
        user.TokenVersion.ShouldBeGreaterThan(0);

        var tokens = await context.RefreshTokens.Where(t => t.UserId == _mentorId).ToListAsync();
        tokens.ShouldAllBe(t => t.RevokedAt != null);
        tokens.ShouldAllBe(t => t.ReasonRevoked == RefreshTokenRevocationReason.CategoryChanged);

        var audit = await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.UserChangeCategory);
        audit.BranchId.ShouldBe(_headOfficeId);
        audit.CategoryId.ShouldBe(_pythonId);
        audit.Metadata!.RootElement.GetProperty("reason").GetString().ShouldBe(Reason);
    }

    /// <summary>
    /// <c>USER-012</c>: the answer names the blocking tasks and nothing else — no titles, no other
    /// people (<c>BRN-039</c>).
    /// </summary>
    [Fact]
    public async Task Unfinished_work_blocks_a_category_transfer()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-category",
            new ChangeCategoryRequest(_pythonId, Reason, await TokenOfAsync(admin, _mentorId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("code").GetString().ShouldBe(ErrorCodes.CategoryChangeBlocked);
        document.RootElement.GetProperty("reason").GetString().ShouldBe(TransferBlockReasons.ActiveAssignments);

        var blocking = document.RootElement.GetProperty("blockingAssignmentIds")
            .EnumerateArray()
            .Select(e => e.GetGuid())
            .ToList();

        blocking.ShouldHaveSingleItem().ShouldBe(assignmentId);

        // The payload is for navigation, not for reporting: no title of the blocking task anywhere.
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("Задача");
    }

    [Fact]
    public async Task A_finished_task_does_not_block_a_category_transfer()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var assignment = await ReadAsync<AssignmentDto>(await lead.GetAsync($"/api/v1/assignments/{assignmentId}"));

        await lead.PostAsJsonAsync($"/api/v1/assignments/{assignmentId}/cancel",
            new CancelAssignmentRequest("Отменено, ментор переводится", assignment.ConcurrencyToken));

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-category",
            new ChangeCategoryRequest(_pythonId, Reason, await TokenOfAsync(admin, _mentorId)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary><c>USER-013</c>: a category cannot be left without the Lead who runs it.</summary>
    [Fact]
    public async Task An_active_lead_cannot_be_moved_to_another_category()
    {
        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_leadId}/change-category",
            new ChangeCategoryRequest(_pythonId, Reason, await TokenOfAsync(admin, _leadId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("code").GetString().ShouldBe(ErrorCodes.CategoryChangeBlocked);
        document.RootElement.GetProperty("reason").GetString().ShouldBe(TransferBlockReasons.ActiveLead);
    }

    /// <summary><c>USER-037</c>: change-category is not a back door into another branch.</summary>
    [Fact]
    public async Task A_category_of_another_branch_is_refused()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-category",
            new ChangeCategoryRequest(_khujandCategoryId, Reason, await TokenOfAsync(admin, _mentorId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.CrossScopeReference);
    }

    [Fact]
    public async Task A_deactivated_category_cannot_receive_the_user()
    {
        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-category",
            new ChangeCategoryRequest(_archivedId, Reason, await TokenOfAsync(admin, _mentorId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.CategoryInactive);
    }

    /// <summary>
    /// A move to the category the user already sits in would revoke every session to change nothing.
    /// </summary>
    [Fact]
    public async Task A_transfer_to_the_same_category_is_a_validation_failure()
    {
        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-category",
            new ChangeCategoryRequest(_sharpId, Reason, await TokenOfAsync(admin, _mentorId)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("нет")]
    public async Task The_reason_is_mandatory(string reason)
    {
        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-category",
            new ChangeCategoryRequest(_pythonId, reason, await TokenOfAsync(admin, _mentorId)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// <c>USER-016</c>: the mentor keeps reading their own past work, and the Lead of the category they
    /// joined never gains sight of it — the assignment's category is immutable (10.6.4).
    /// </summary>
    [Fact]
    public async Task Historical_work_stays_readable_by_its_owner_after_a_category_transfer()
    {
        var assignmentId = await PublishAssignmentAsync();
        await ApproveAsync(assignmentId);

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");
        (await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-category",
                new ChangeCategoryRequest(_pythonId, Reason, await TokenOfAsync(admin, _mentorId))))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        (await mentor.GetAsync($"/api/v1/assignments/{assignmentId}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var pythonLead = await SignInAsync("lead-python@mentortaskflow.test");
        (await pythonLead.GetAsync($"/api/v1/assignments/{assignmentId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------
    // Branch transfer (TZ 39.6)
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>BRN-036</c>, <c>USER-030</c>: 403 for a Branch Admin, and for their <b>own</b> branch as much
    /// as anyone else's.
    /// </summary>
    [Fact]
    public async Task A_branch_admin_cannot_transfer_between_branches()
    {
        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch",
            new ChangeBranchRequest(_khujandId, Reason, "MQ", _khujandCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary><c>BRN-048</c>: the whole sequence, in one transaction.</summary>
    [Fact]
    public async Task A_branch_transfer_moves_the_user_and_records_it()
    {
        await SeedRefreshTokenAsync(_mentorId);
        await SeedSecurityTokenAsync(_mentorId);

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch",
            new ChangeBranchRequest(_khujandId, Reason, await TokenOfAsync(admin, _mentorId), _khujandCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var moved = await ReadAsync<UserDto>(response);
        moved.BranchId.ShouldBe(_khujandId);
        moved.CategoryId.ShouldBe(_khujandCategoryId);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var branchHistory = await context.UserBranchHistory.SingleAsync(h => h.UserId == _mentorId);
        branchHistory.OldBranchId.ShouldBe(_headOfficeId);
        branchHistory.NewBranchId.ShouldBe(_khujandId);
        branchHistory.OldCategoryId.ShouldBe(_sharpId);
        branchHistory.NewCategoryId.ShouldBe(_khujandCategoryId);

        // USER-025: the category also changed, so both journals carry the move. The category row
        // belongs to the branch the change happened in — the one being left.
        var categoryHistory = await context.UserCategoryHistory.SingleAsync(h => h.UserId == _mentorId);
        categoryHistory.BranchId.ShouldBe(_headOfficeId);
        categoryHistory.CorrelationId.ShouldBe(branchHistory.CorrelationId);

        (await context.RefreshTokens.Where(t => t.UserId == _mentorId).ToListAsync())
            .ShouldAllBe(t => t.ReasonRevoked == RefreshTokenRevocationReason.BranchChanged);

        // AUTH-034: a set-password link issued in the old branch's context dies with the rest.
        (await context.UserSecurityTokens.Where(t => t.UserId == _mentorId).ToListAsync())
            .ShouldAllBe(t => t.InvalidatedAt != null);

        var notification = await context.NotificationOutbox
            .SingleAsync(n => n.EventType == NotificationEventTypes.UserBranchChanged);

        notification.UserId.ShouldBe(_mentorId);
        notification.BranchId.ShouldBe(_khujandId);
    }

    /// <summary>
    /// <c>TEN-049</c>: three records, not one. A Branch Admin must not see the organization-level
    /// transfer — it names the counterpart branch and discloses the composition of the organization —
    /// so each branch gets its own derivative carrying no counterpart at all.
    /// </summary>
    [Fact]
    public async Task A_branch_transfer_leaves_one_organization_record_and_two_branch_derivatives()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch",
            new ChangeBranchRequest(_khujandId, Reason, await TokenOfAsync(admin, _mentorId), _khujandCategoryId));

        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var transfer = await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.UserChangeBranch);
        transfer.BranchId.ShouldBeNull();

        var left = await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.UserLeftBranch);
        left.BranchId.ShouldBe(_headOfficeId);

        var joined = await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.UserJoinedBranch);
        joined.BranchId.ShouldBe(_khujandId);

        // The derivatives carry the fact and nothing more: no counterpart branch in either.
        left.Metadata.ShouldBeNull();
        joined.Metadata.ShouldBeNull();
    }

    /// <summary><c>TEST-TEN-025</c>: one assignment in <c>Assigned</c> is enough to stop the transfer.</summary>
    [Fact]
    public async Task Unfinished_work_blocks_a_branch_transfer()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch",
            new ChangeBranchRequest(_khujandId, Reason, await TokenOfAsync(admin, _mentorId), _khujandCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("code").GetString().ShouldBe(ErrorCodes.BranchChangeBlocked);
        document.RootElement.GetProperty("blockingAssignmentIds")
            .EnumerateArray()
            .Select(e => e.GetGuid())
            .ShouldHaveSingleItem()
            .ShouldBe(assignmentId);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.UserBranchHistory.CountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// <c>TEST-TEN-029</c>: ownership never crosses a branch boundary. The mentor keeps their own tasks
    /// after a category change and loses them after a branch change — the difference of
    /// <c>USER-017</c>.
    /// </summary>
    [Fact]
    public async Task A_transferred_user_loses_sight_of_their_own_former_tasks()
    {
        var assignmentId = await PublishAssignmentAsync();
        await ApproveAsync(assignmentId);

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");
        (await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch",
                new ChangeBranchRequest(_khujandId, Reason, await TokenOfAsync(admin, _mentorId), _khujandCategoryId)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        (await mentor.GetAsync($"/api/v1/assignments/{assignmentId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var page = await ReadAsync<PagedResult<AssignmentDto>>(await mentor.GetAsync("/api/v1/assignments"));
        page.Items.ShouldBeEmpty();

        // BRN-049: the assignment itself did not move. It stays a fact of the branch that created it.
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Assignments.SingleAsync(a => a.Id == assignmentId)).BranchId.ShouldBe(_headOfficeId);
    }

    /// <summary>
    /// <c>TEST-TEN-034</c>: two simultaneous transfers of one user leave exactly one history row.
    /// </summary>
    /// <remarks>
    /// Both requests present the same token. The row lock of <c>BRN-048</c> serialises them, so the
    /// loser reads the settled state and finds its token stale rather than overwriting the winner.
    /// </remarks>
    [Fact]
    public async Task Two_simultaneous_branch_transfers_produce_one_history_row()
    {
        using var first = await SignInAsync("organization-admin@mentortaskflow.test");
        using var second = await SignInAsync("organization-admin@mentortaskflow.test");

        var token = await TokenOfAsync(first, _mentorId);
        var request = new ChangeBranchRequest(_khujandId, Reason, token, _khujandCategoryId);

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch", request),
            second.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch", request));

        responses.Count(r => r.StatusCode is HttpStatusCode.OK).ShouldBe(1);

        var loser = responses.Single(r => r.StatusCode is not HttpStatusCode.OK);
        loser.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(loser)).ShouldBe(ErrorCodes.ConcurrencyConflict);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.UserBranchHistory.CountAsync(h => h.UserId == _mentorId)).ShouldBe(1);
    }

    // -----------------------------------------------------------------
    // Validation of the target (BRN-037)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_mentor_transfer_requires_a_category_in_the_new_branch()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch",
            new ChangeBranchRequest(_khujandId, Reason, await TokenOfAsync(admin, _mentorId)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    /// <summary>A category of the branch being left is not a category of the branch being joined.</summary>
    [Fact]
    public async Task The_target_category_must_belong_to_the_target_branch()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch",
            new ChangeBranchRequest(_khujandId, Reason, await TokenOfAsync(admin, _mentorId), _pythonId));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.CrossScopeReference);
    }

    [Fact]
    public async Task A_deactivated_branch_cannot_receive_the_user()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var khujand = await ReadAsync<Contracts.Tenancy.BranchDto>(await admin.GetAsync($"/api/v1/branches/{_khujandId}"));

        (await admin.PostAsJsonAsync($"/api/v1/branches/{_khujandId}/deactivate",
                new Contracts.Tenancy.DeactivateBranchRequest(khujand.ConcurrencyToken, ConfirmActiveUsers: true)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch",
            new ChangeBranchRequest(_khujandId, Reason, await TokenOfAsync(admin, _mentorId), _khujandCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.BranchInactive);
    }

    [Fact]
    public async Task An_unknown_branch_is_indistinguishable_from_one_of_another_organization()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_mentorId}/change-branch",
            new ChangeBranchRequest(Guid.CreateVersion7(), Reason, await TokenOfAsync(admin, _mentorId), _khujandCategoryId));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary><c>USER-033</c>: an Organization Admin changes contour through change-role instead.</summary>
    [Fact]
    public async Task An_organization_admin_is_not_transferred_by_this_operation()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var self = await ReadAsync<PagedResult<UserDto>>(await admin.GetAsync("/api/v1/users?role=Admin"));
        var organizationAdmin = self.Items.Single(u => u.AdminScope == nameof(AdminScope.Organization));

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{organizationAdmin.Id}/change-branch",
            new ChangeBranchRequest(_khujandId, Reason, organizationAdmin.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// The scope guard survived being re-expressed as a trigger: a transfer is allowed, handing work
    /// across a scope boundary still is not.
    /// </summary>
    /// <remarks>
    /// The composite FK of TZ 12.2a could not tell the two apart — it refused the transfer precisely
    /// because historical assignments still pointed at the old tuple. The trigger fires on INSERT and
    /// on a change of the executor columns, and never when the users row moves.
    /// </remarks>
    [Fact]
    public async Task The_database_still_refuses_to_hand_work_across_a_scope_boundary()
    {
        var assignmentId = await PublishAssignmentAsync();

        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE assignments SET assigned_to_id = @assignee WHERE id = @id";
        command.Parameters.AddWithValue("id", assignmentId);

        // The Python lead of the same branch: a real user, wrong category.
        command.Parameters.AddWithValue("assignee", await IdOfAsync("lead-python@mentortaskflow.test"));

        var exception = await Should.ThrowAsync<Npgsql.PostgresException>(command.ExecuteNonQueryAsync());

        exception.SqlState.ShouldBe(Npgsql.PostgresErrorCodes.ForeignKeyViolation);
        exception.ConstraintName.ShouldBe("fk_assignments_assignee_scope");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<Guid> IdOfAsync(string email)
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        return await context.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    private async Task<Guid> PublishAssignmentAsync()
    {
        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var draft = await ReadAsync<AssignmentDto>(await lead.PostAsJsonAsync("/api/v1/assignments/drafts",
            new CreateAssignmentDraftRequest(_mentorId, null, "Задача ментора", null, null)));

        var published = await ReadAsync<AssignmentDto>(await lead.PostAsJsonAsync(
            $"/api/v1/assignments/{draft.Id}/publish",
            new AssignmentActionRequest(draft.ConcurrencyToken)));

        return published.Id;
    }

    /// <summary>Drives an assignment to <c>Approved</c> so it stops blocking transfers.</summary>
    private async Task ApproveAsync(Guid assignmentId)
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var assignment = await context.Assignments.SingleAsync(a => a.Id == assignmentId);
        var now = DateTimeOffset.UtcNow;

        assignment.Submit(isFirstVersion: true, now);
        assignment.StartReview(now);
        assignment.Approve(now);

        await context.SaveChangesAsync();
    }

    private static async Task<string> TokenOfAsync(HttpClient admin, Guid userId) =>
        (await ReadAsync<UserDto>(await admin.GetAsync($"/api/v1/users/{userId}"))).ConcurrencyToken;

    private async Task SeedRefreshTokenAsync(Guid userId)
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        context.RefreshTokens.Add(RefreshToken.IssueNewFamily(
            userId,
            $"hash-{Guid.CreateVersion7():N}",
            TimeSpan.FromDays(30),
            createdByIp: null,
            DateTimeOffset.UtcNow));

        await context.SaveChangesAsync();
    }

    private async Task SeedSecurityTokenAsync(Guid userId)
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        context.UserSecurityTokens.Add(UserSecurityToken.Issue(
            userId,
            SecurityTokenPurpose.SetPassword,
            $"hash-{Guid.CreateVersion7():N}",
            TimeSpan.FromHours(24),
            createdByIp: null,
            DateTimeOffset.UtcNow));

        await context.SaveChangesAsync();
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

        var sharp = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        var python = Category.Create(organization.Id, headOffice.Id, "Python", null, Seeded);
        var archived = Category.Create(organization.Id, headOffice.Id, "Design", null, Seeded);
        var khujandCategory = Category.Create(organization.Id, khujand.Id, "C#", null, Seeded);
        archived.Deactivate(Seeded);

        context.Categories.AddRange(sharp, python, archived, khujandCategory);

        foreach (var (category, branch) in new[]
                 {
                     (sharp, headOffice), (python, headOffice), (archived, headOffice), (khujandCategory, khujand),
                 })
        {
            context.CategorySettings.Add(CategorySettings.CreateDefault(category, branch.TimeZoneId, Seeded));
        }

        var organizationAdmin = User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded);
        var branchAdmin = User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded);
        var sharpLead = User.CreateLead(organization.Id, headOffice.Id, sharp.Id, "Лид C#", "lead-sharp@mentortaskflow.test", Seeded);
        var pythonLead = User.CreateLead(organization.Id, headOffice.Id, python.Id, "Лид Python", "lead-python@mentortaskflow.test", Seeded);
        var mentor = User.CreateMentor(organization.Id, headOffice.Id, sharp.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded);

        var users = new List<User> { organizationAdmin, branchAdmin, sharpLead, pythonLead, mentor };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        _headOfficeId = headOffice.Id;
        _khujandId = khujand.Id;
        _sharpId = sharp.Id;
        _pythonId = python.Id;
        _archivedId = archived.Id;
        _khujandCategoryId = khujandCategory.Id;
        _mentorId = mentor.Id;
        _leadId = sharpLead.Id;
    }
}
