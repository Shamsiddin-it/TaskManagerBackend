using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Assignments;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Schedule;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MentorTaskFlow.IntegrationTests.Assignments;

/// <summary>The assignment lifecycle end to end (Приложение D.5, TZ 13).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AssignmentLifecycleTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string BranchHeader = "X-MTF-Branch-Id";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _headOfficeId;
    private Guid _headCategoryId;
    private Guid _headMentorId;
    private Guid _secondHeadMentorId;
    private Guid _khujandMentorId;
    private Guid _templateId;

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
    // Creation and publication
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_lead_creates_a_draft_and_publishes_it()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");

        var draft = await CreateDraftAsync(lead);
        draft.Status.ShouldBe(nameof(AssignmentStatus.Draft));
        draft.AssignedAt.ShouldBeNull();

        var published = await ReadAsync<AssignmentDto>(await lead.PostAsJsonAsync(
            $"/api/v1/assignments/{draft.Id}/publish",
            new AssignmentActionRequest(draft.ConcurrencyToken)));

        published.Status.ShouldBe(nameof(AssignmentStatus.Assigned));
        published.AssignedAt.ShouldNotBeNull();
        published.AssignedById.ShouldNotBeNull();
    }

    /// <summary>
    /// 10.6.1: the template's title is copied at creation and stops tracking it, so a later edit of
    /// the template leaves published work alone.
    /// </summary>
    [Fact]
    public async Task A_draft_built_from_a_template_snapshots_its_title()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");

        var draft = await ReadAsync<AssignmentDto>(await lead.PostAsJsonAsync("/api/v1/assignments/drafts",
            new CreateAssignmentDraftRequest(_headMentorId, _templateId, null, null, null)));

        draft.Title.ShouldBe("Домашнее задание");

        await lead.PutAsJsonAsync($"/api/v1/topic-assignments/{_templateId}",
            new Contracts.Schedule.UpdateTopicAssignmentRequest(
                "HomeTask", "Переписанный шаблон", null, true,
                (await ReadAsync<Contracts.Schedule.TopicAssignmentDto>(
                    await lead.GetAsync($"/api/v1/topic-assignments/{_templateId}"))).ConcurrencyToken));

        var reloaded = await ReadAsync<AssignmentDto>(await lead.GetAsync($"/api/v1/assignments/{draft.Id}"));
        reloaded.Title.ShouldBe("Домашнее задание");
    }

    /// <summary>
    /// <c>ASN-027</c>: with no deadline given, the category's default days and
    /// <c>DefaultDueTimeLocal</c> decide it — the field 2.1 added because «PlannedDate + DueDays»
    /// never said what time of day the work was due.
    /// </summary>
    [Fact]
    public async Task An_omitted_deadline_falls_back_to_the_category_default()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");

        var draft = await CreateDraftAsync(lead);

        // Asia/Dushanbe is UTC+5, the default is 3 days at 23:59 local → 18:59 UTC.
        draft.InitialDueAt.Minute.ShouldBe(59);
        draft.InitialDueAt.UtcDateTime.Hour.ShouldBe(18);
        draft.CurrentDueAt.ShouldBe(draft.InitialDueAt);
    }

    [Fact]
    public async Task A_deadline_in_the_past_is_refused()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync("/api/v1/assignments/drafts",
            new CreateAssignmentDraftRequest(_headMentorId, null, "Задача", null, DateTimeOffset.UtcNow.AddDays(-1)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    // -----------------------------------------------------------------
    // Who may act (ASN-025)
    // -----------------------------------------------------------------

    /// <summary>
    /// The study cycle belongs to the Lead. Version 2.0's «Lead/Admin — управление» wrongly widened
    /// Admin's reach and was removed in 2.1.
    /// </summary>
    [Theory]
    [InlineData("organization-admin@mentortaskflow.test")]
    [InlineData("branch-admin-head@mentortaskflow.test")]
    [InlineData("mentor-head@mentortaskflow.test")]
    public async Task Nobody_but_the_lead_creates_a_draft(string email)
    {
        using var client = await SignInAsync(email);

        var response = await client.PostAsJsonAsync("/api/v1/assignments/drafts",
            new CreateAssignmentDraftRequest(_headMentorId, null, "Задача", null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_mentor_cannot_cancel_their_own_task()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var assignment = await PublishedAsync(lead);

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var visible = await ReadAsync<AssignmentDto>(await mentor.GetAsync($"/api/v1/assignments/{assignment.Id}"));

        var response = await mentor.PostAsJsonAsync($"/api/v1/assignments/{assignment.Id}/cancel",
            new CancelAssignmentRequest("Не хочу это делать", visible.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// <c>ASN-025</c>: force-cancel is the single administrative action inside the study cycle, and it
    /// is audited because it is administrative — ordinary lifecycle moves live in TaskEvent instead
    /// (<c>AUD-004</c>).
    /// </summary>
    [Fact]
    public async Task An_admin_force_cancel_is_audited()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var assignment = await PublishedAsync(lead);

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var visible = await ReadAsync<AssignmentDto>(await admin.GetAsync($"/api/v1/assignments/{assignment.Id}"));

        var response = await admin.PostAsJsonAsync($"/api/v1/assignments/{assignment.Id}/cancel",
            new CancelAssignmentRequest("Задача заблокирована, закрываем", visible.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<AssignmentDto>(response)).Status.ShouldBe(nameof(AssignmentStatus.Cancelled));

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var audit = await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.AssignmentForceCancel);

        audit.EntityId.ShouldBe(assignment.Id);
        audit.BranchId.ShouldBe(_headOfficeId);
        audit.Metadata!.RootElement.GetProperty("previousStatus").GetString().ShouldBe(nameof(AssignmentStatus.Assigned));
    }

    // -----------------------------------------------------------------
    // Visibility (13.2, level 4 of TZ 9.1)
    // -----------------------------------------------------------------

    /// <summary>
    /// A suggestion is a proposal the Lead has not accepted; announcing it would tell the mentor
    /// about work that may never be handed out (13.2).
    /// </summary>
    [Fact]
    public async Task A_mentor_sees_neither_drafts_nor_suggestions()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var draft = await CreateDraftAsync(lead);
        var suggestionId = await SeedSuggestionAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<AssignmentDto>>(await mentor.GetAsync("/api/v1/assignments"));
        page.Items.ShouldBeEmpty();

        (await mentor.GetAsync($"/api/v1/assignments/{draft.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await mentor.GetAsync($"/api/v1/assignments/{suggestionId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_mentor_sees_their_own_published_task_and_not_a_colleagues()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");

        var mine = await PublishedAsync(lead);
        var theirs = await PublishedAsync(lead, assigneeId: _secondHeadMentorId);

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<AssignmentDto>>(await mentor.GetAsync("/api/v1/assignments"));
        page.Items.ShouldHaveSingleItem().Id.ShouldBe(mine.Id);

        (await mentor.GetAsync($"/api/v1/assignments/{theirs.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Filtering by another mentor cannot widen a mentor's own view past level 4 of TZ 9.1.</summary>
    [Fact]
    public async Task A_mentor_cannot_widen_their_view_with_a_filter()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        await PublishedAsync(lead, assigneeId: _secondHeadMentorId);

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<AssignmentDto>>(
            await mentor.GetAsync($"/api/v1/assignments?assignedToId={_secondHeadMentorId}"));

        page.Items.ShouldBeEmpty();
    }

    // -----------------------------------------------------------------
    // Cross-scope (TEN-024, TEST-TEN-014)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_mentor_of_another_branch_cannot_be_assigned()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync("/api/v1/assignments/drafts",
            new CreateAssignmentDraftRequest(_khujandMentorId, null, "Задача", null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.CrossScopeReference);
    }

    /// <summary>
    /// <c>TEST-TEN-014</c>: the check is not the only guard. A direct INSERT that bypasses the
    /// application is refused by <c>fk_assignments_assignee_scope</c>.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_a_cross_branch_assignee()
    {
        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO assignments
                (id, organization_id, branch_id, category_id, assigned_to_id, title, status, source,
                 initial_due_at, current_due_at, last_event_sequence, created_at, updated_at)
            SELECT @id, o.id, @branchId, @categoryId, @assigneeId, 'Задача', 'Draft', 'Manual',
                   now() + interval '3 days', now() + interval '3 days', 0, now(), now()
            FROM organizations o
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("branchId", _headOfficeId);
        command.Parameters.AddWithValue("categoryId", _headCategoryId);

        // A mentor of the Khujand branch, while the row claims the head office.
        command.Parameters.AddWithValue("assigneeId", _khujandMentorId);

        var exception = await Should.ThrowAsync<PostgresException>(command.ExecuteNonQueryAsync());

        exception.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        exception.ConstraintName.ShouldBe("fk_assignments_assignee_scope");
    }

    [Fact]
    public async Task A_deactivated_mentor_cannot_receive_work()
    {
        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var mentor = await ReadAsync<Contracts.Users.UserDto>(await admin.GetAsync($"/api/v1/users/{_headMentorId}"));

        await admin.PostAsJsonAsync($"/api/v1/users/{_headMentorId}/deactivate",
            new Contracts.Users.UserActionRequest(mentor.ConcurrencyToken));

        using var lead = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync("/api/v1/assignments/drafts",
            new CreateAssignmentDraftRequest(_headMentorId, null, "Задача", null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.AssigneeInactive);
    }

    // -----------------------------------------------------------------
    // Events (EVT-002, EVT-004, EVT-005)
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>EVT-005</c>: accepting a suggestion produces two events sharing a correlation id and taking
    /// consecutive sequence numbers — the decision and the publication are separate facts.
    /// </summary>
    [Fact]
    public async Task Accepting_a_suggestion_records_two_correlated_events()
    {
        var suggestionId = await SeedSuggestionAsync();

        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var suggestion = await ReadAsync<AssignmentDto>(await lead.GetAsync($"/api/v1/assignments/{suggestionId}"));

        await lead.PostAsJsonAsync($"/api/v1/assignments/{suggestionId}/accept-suggestion",
            new AssignmentActionRequest(suggestion.ConcurrencyToken));

        var history = await ReadAsync<List<TaskEventDto>>(
            await lead.GetAsync($"/api/v1/assignments/{suggestionId}/history"));

        var accepted = history.Single(e => e.EventType == nameof(TaskEventType.SuggestionAccepted));
        var assigned = history.Single(e => e.EventType == nameof(TaskEventType.Assigned));

        accepted.CorrelationId.ShouldBe(assigned.CorrelationId);
        assigned.SequenceNumber.ShouldBe(accepted.SequenceNumber + 1);
    }

    /// <summary><c>EVT-002</c>: every status change leaves an event behind.</summary>
    [Fact]
    public async Task Every_transition_leaves_an_event()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var assignment = await PublishedAsync(lead);

        var history = await ReadAsync<List<TaskEventDto>>(
            await lead.GetAsync($"/api/v1/assignments/{assignment.Id}/history"));

        history.Select(e => e.EventType).ShouldBe([
            nameof(TaskEventType.DraftCreated),
            nameof(TaskEventType.Assigned),
        ]);

        // Sequence numbers rise from 1 without gaps (12.4).
        history.Select(e => e.SequenceNumber).ShouldBe([1, 2]);
    }

    /// <summary>
    /// <c>EVT-004</c>: a mentor sees the role of whoever acted, not their identity. The history has to
    /// explain what happened to their task without naming other people.
    /// </summary>
    [Fact]
    public async Task A_mentor_sees_roles_in_the_history_rather_than_identities()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var assignment = await PublishedAsync(lead);

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var history = await ReadAsync<List<TaskEventDto>>(
            await mentor.GetAsync($"/api/v1/assignments/{assignment.Id}/history"));

        history.ShouldNotBeEmpty();
        history.ShouldAllBe(e => e.ActorId == null);
        history.ShouldAllBe(e => e.ActorLabel != null);
    }

    // -----------------------------------------------------------------
    // Notifications and reassignment
    // -----------------------------------------------------------------

    [Fact]
    public async Task Publishing_notifies_the_assignee()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var assignment = await PublishedAsync(lead);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var notification = await context.NotificationOutbox
            .SingleAsync(n => n.EventType == NotificationEventTypes.AssignmentAssigned);

        notification.UserId.ShouldBe(_headMentorId);
        notification.BranchId.ShouldBe(_headOfficeId);
        notification.CategoryId.ShouldBe(_headCategoryId);
    }

    /// <summary>Nobody needs telling that a draft changed hands — it was never theirs (Приложение B).</summary>
    [Fact]
    public async Task Reassigning_a_draft_notifies_nobody()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var draft = await CreateDraftAsync(lead);

        await lead.PostAsJsonAsync($"/api/v1/assignments/{draft.Id}/reassign",
            new ReassignAssignmentRequest(_secondHeadMentorId, draft.ConcurrencyToken));

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.NotificationOutbox.CountAsync(
            n => n.EventType == NotificationEventTypes.AssignmentReassigned)).ShouldBe(0);
    }

    [Fact]
    public async Task Reassigning_a_published_task_notifies_both_mentors()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var assignment = await PublishedAsync(lead);

        var response = await lead.PostAsJsonAsync($"/api/v1/assignments/{assignment.Id}/reassign",
            new ReassignAssignmentRequest(_secondHeadMentorId, assignment.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<AssignmentDto>(response)).AssignedToId.ShouldBe(_secondHeadMentorId);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var recipients = await context.NotificationOutbox
            .Where(n => n.EventType == NotificationEventTypes.AssignmentReassigned)
            .Select(n => n.UserId)
            .ToListAsync();

        recipients.ShouldBe([_headMentorId, _secondHeadMentorId], ignoreOrder: true);
    }

    // -----------------------------------------------------------------
    // Concurrency and terminal states
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_stale_token_is_a_conflict()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var draft = await CreateDraftAsync(lead);

        await lead.PostAsJsonAsync($"/api/v1/assignments/{draft.Id}/publish",
            new AssignmentActionRequest(draft.ConcurrencyToken));

        var replay = await lead.PostAsJsonAsync($"/api/v1/assignments/{draft.Id}/publish",
            new AssignmentActionRequest(draft.ConcurrencyToken));

        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(replay)).ShouldBe(ErrorCodes.ConcurrencyConflict);
    }

    /// <summary><c>ASN-021</c>: the terminal code, not the generic transition error.</summary>
    [Fact]
    public async Task An_action_on_a_cancelled_task_reports_terminal()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var assignment = await PublishedAsync(lead);

        var cancelled = await ReadAsync<AssignmentDto>(await lead.PostAsJsonAsync(
            $"/api/v1/assignments/{assignment.Id}/cancel",
            new CancelAssignmentRequest("Отменено по решению тимлида", assignment.ConcurrencyToken)));

        var response = await lead.PostAsJsonAsync($"/api/v1/assignments/{assignment.Id}/start-review",
            new AssignmentActionRequest(cancelled.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.AssignmentTerminal);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<AssignmentDto> CreateDraftAsync(HttpClient lead, Guid? assigneeId = null)
    {
        var response = await lead.PostAsJsonAsync("/api/v1/assignments/drafts",
            new CreateAssignmentDraftRequest(assigneeId ?? _headMentorId, null, "Задача", "Описание", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return await ReadAsync<AssignmentDto>(response);
    }

    private async Task<AssignmentDto> PublishedAsync(HttpClient lead, Guid? assigneeId = null)
    {
        var draft = await CreateDraftAsync(lead, assigneeId);

        return await ReadAsync<AssignmentDto>(await lead.PostAsJsonAsync(
            $"/api/v1/assignments/{draft.Id}/publish",
            new AssignmentActionRequest(draft.ConcurrencyToken)));
    }

    /// <summary>
    /// Plants a scheduler suggestion directly.
    /// </summary>
    /// <remarks>
    /// The scheduler itself arrives in a later phase, so the row is created through the domain factory
    /// — the same one the job will use — rather than through an endpoint that does not exist yet.
    /// </remarks>
    private async Task<Guid> SeedSuggestionAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var suggestion = Assignment.CreateSuggestion(
            (await context.Organizations.FirstAsync()).Id,
            _headOfficeId,
            _headCategoryId,
            _headMentorId,
            _templateId,
            "Предложение планировщика",
            null,
            DateTimeOffset.UtcNow.AddDays(3),
            DateOnly.FromDateTime(DateTime.UtcNow),
            $"{Guid.CreateVersion7():N}",
            DateTimeOffset.UtcNow);

        context.Assignments.Add(suggestion);
        await context.SaveChangesAsync();

        return suggestion.Id;
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

        var headCategory = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        var khujandCategory = Category.Create(organization.Id, khujand.Id, "C#", null, Seeded);
        context.Categories.AddRange(headCategory, khujandCategory);

        context.CategorySettings.AddRange(
            CategorySettings.CreateDefault(headCategory, headOffice.TimeZoneId, Seeded),
            CategorySettings.CreateDefault(khujandCategory, khujand.TimeZoneId, Seeded));

        var topic = Topic.Create(organization.Id, headOffice.Id, headCategory.Id, 1, null, "Введение в C#", null, Seeded);
        context.Topics.Add(topic);

        var template = TopicAssignment.Create(topic, TopicAssignmentType.HomeTask, "Домашнее задание", "Описание", true, Seeded);
        context.TopicAssignments.Add(template);

        var organizationAdmin = User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded);
        var branchAdmin = User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded);
        var lead = User.CreateLead(organization.Id, headOffice.Id, headCategory.Id, "Лид", "lead-head@mentortaskflow.test", Seeded);
        var mentor = User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded);
        var secondMentor = User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Второй ментор", "mentor2-head@mentortaskflow.test", Seeded);
        var khujandMentor = User.CreateMentor(organization.Id, khujand.Id, khujandCategory.Id, "Ментор Худжанда", "mentor-khujand@mentortaskflow.test", Seeded);

        var users = new List<User> { organizationAdmin, branchAdmin, lead, mentor, secondMentor, khujandMentor };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        _headOfficeId = headOffice.Id;
        _headCategoryId = headCategory.Id;
        _headMentorId = mentor.Id;
        _secondHeadMentorId = secondMentor.Id;
        _khujandMentorId = khujandMentor.Id;
        _templateId = template.Id;
    }
}
