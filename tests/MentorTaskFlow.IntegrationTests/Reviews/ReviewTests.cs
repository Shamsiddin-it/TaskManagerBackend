using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MentorTaskFlow.Contracts.Assignments;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Reviews;
using MentorTaskFlow.Contracts.Submissions;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Reviews;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MentorTaskFlow.IntegrationTests.Reviews;

/// <summary>The Lead's decision on a submitted version (TZ 15.7, 10.8).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string Comment = "Переделайте раздел про индексы: не хватает обоснования выбора.";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _organizationId;
    private Guid _headOfficeId;
    private Guid _sharpId;
    private Guid _pythonId;
    private Guid _mentorId;
    private Guid _sharpLeadId;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        await minio.ResetAsync();
        await SeedAsync();

        _factory = new MentorTaskFlowApiFactory
        {
            ConnectionStringOverride = postgres.ConnectionString,
            StorageEndpointOverride = minio.Endpoint,
        };
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------
    // Approval
    // -----------------------------------------------------------------

    /// <summary><c>REV-002</c>: the decision, the status, the event and the notification, together.</summary>
    [Fact]
    public async Task An_approval_closes_the_task()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(nameof(ReviewDecision.Approved), await TokenOfAsync(lead, work.AssignmentId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var review = await ReadAsync<ReviewDto>(response);
        review.Decision.ShouldBe(nameof(ReviewDecision.Approved));
        review.ReworkDueAt.ShouldBeNull();
        review.ReviewerId.ShouldBe(_sharpLeadId);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var assignment = await context.Assignments.SingleAsync(a => a.Id == work.AssignmentId);
        assignment.Status.ShouldBe(AssignmentStatus.Approved);
        assignment.ApprovedAt.ShouldNotBeNull();

        var notification = await context.NotificationOutbox
            .SingleAsync(n => n.EventType == NotificationEventTypes.ReviewApproved);

        notification.UserId.ShouldBe(_mentorId);

        (await context.TaskEvents.AnyAsync(e => e.EventType == TaskEventType.ReviewApproved)).ShouldBeTrue();
    }

    /// <summary>A comment on an approval is welcome; a new deadline on one is a contradiction.</summary>
    [Fact]
    public async Task An_approval_may_carry_a_comment()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var review = await ReadAsync<ReviewDto>(await lead.PostAsJsonAsync(
            $"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(
                nameof(ReviewDecision.Approved),
                await TokenOfAsync(lead, work.AssignmentId),
                Comment: "Хорошая работа, замечаний нет.")));

        review.Comment.ShouldBe("Хорошая работа, замечаний нет.");
    }

    [Fact]
    public async Task An_approval_with_a_rework_deadline_is_refused()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(
                nameof(ReviewDecision.Approved),
                await TokenOfAsync(lead, work.AssignmentId),
                ReworkDueAt: DateTimeOffset.UtcNow.AddDays(3)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    // -----------------------------------------------------------------
    // Rework
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>REV-002</c>: rework is the one transition that moves <c>CurrentDueAt</c> (14.1).
    /// </summary>
    [Fact]
    public async Task Rework_returns_the_task_and_moves_its_deadline()
    {
        var work = await WorkAwaitingReviewAsync();
        var reworkDueAt = DateTimeOffset.UtcNow.AddDays(30);

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var review = await ReadAsync<ReviewDto>(await lead.PostAsJsonAsync(
            $"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(
                nameof(ReviewDecision.NeedsRework),
                await TokenOfAsync(lead, work.AssignmentId),
                Comment,
                reworkDueAt)));

        review.Decision.ShouldBe(nameof(ReviewDecision.NeedsRework));
        review.Comment.ShouldBe(Comment);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var assignment = await context.Assignments.SingleAsync(a => a.Id == work.AssignmentId);
        assignment.Status.ShouldBe(AssignmentStatus.NeedsRework);
        assignment.CurrentDueAt.ShouldBe(review.ReworkDueAt!.Value, TimeSpan.FromSeconds(1));

        // InitialDueAt is immutable after publication: the original commitment stays visible next to
        // the extended one (14.1).
        assignment.InitialDueAt.ShouldBeLessThan(assignment.CurrentDueAt);

        var recorded = await context.TaskEvents
            .SingleAsync(e => e.EventType == TaskEventType.ReviewNeedsRework);

        recorded.Metadata!.RootElement.GetProperty("newCurrentDueAt").GetDateTimeOffset()
            .ShouldBe(assignment.CurrentDueAt, TimeSpan.FromSeconds(1));

        recorded.ReviewId.ShouldBe(review.Id);
        recorded.SubmissionId.ShouldBe(work.SubmissionId);
    }

    [Fact]
    public async Task Rework_notifies_the_mentor()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(
                nameof(ReviewDecision.NeedsRework),
                await TokenOfAsync(lead, work.AssignmentId),
                Comment,
                DateTimeOffset.UtcNow.AddDays(30)));

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var notification = await context.NotificationOutbox
            .SingleAsync(n => n.EventType == NotificationEventTypes.ReviewNeedsRework);

        notification.UserId.ShouldBe(_mentorId);

        // NTF-005: review text can be long and blunt, and email is not where it belongs. The
        // notification says a decision exists; the interface shows what it was.
        notification.Payload.RootElement.ToString().ShouldNotContain("индексы");
    }

    [Theory]
    [InlineData(null, "Комментарий обязателен")]
    [InlineData("коротко", "Комментарий слишком короткий")]
    public async Task Rework_without_a_usable_comment_is_refused(string? comment, string _)
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(
                nameof(ReviewDecision.NeedsRework),
                await TokenOfAsync(lead, work.AssignmentId),
                comment,
                DateTimeOffset.UtcNow.AddDays(30)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rework_without_a_deadline_is_refused()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(
                nameof(ReviewDecision.NeedsRework),
                await TokenOfAsync(lead, work.AssignmentId),
                Comment));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>A deadline already past would return work that is overdue the moment it arrives.</summary>
    [Fact]
    public async Task Rework_with_a_deadline_in_the_past_is_refused()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(
                nameof(ReviewDecision.NeedsRework),
                await TokenOfAsync(lead, work.AssignmentId),
                Comment,
                DateTimeOffset.UtcNow.AddDays(-1)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>The full loop: rework, a new version, and a decision on that one.</summary>
    [Fact]
    public async Task A_returned_task_accepts_a_new_version_and_a_second_decision()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(
                nameof(ReviewDecision.NeedsRework),
                await TokenOfAsync(lead, work.AssignmentId),
                Comment,
                DateTimeOffset.UtcNow.AddDays(30)));

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var second = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, work.AssignmentId, Pdf("вторая версия"), "вторая.pdf"));

        second.VersionNumber.ShouldBe(2);

        await StartReviewAsync(lead, work.AssignmentId);

        var response = await lead.PostAsJsonAsync($"/api/v1/submissions/{second.Id}/reviews",
            new CreateReviewRequest(nameof(ReviewDecision.Approved), await TokenOfAsync(lead, work.AssignmentId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    // -----------------------------------------------------------------
    // When a decision may be made (REV-003, REV-004, REV-005)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_task_that_is_not_under_review_cannot_be_decided()
    {
        var work = await WorkAwaitingReviewAsync(startReview: false);

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(nameof(ReviewDecision.Approved), await TokenOfAsync(lead, work.AssignmentId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.AssignmentInvalidStatusTransition);
    }

    /// <summary>
    /// <c>REV-004</c>: a verdict on an older version would leave the work the mentor actually submitted
    /// last without a decision, while the task moved on as if it had one.
    /// </summary>
    [Fact]
    public async Task Only_the_latest_version_may_be_decided()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(
                nameof(ReviewDecision.NeedsRework),
                await TokenOfAsync(lead, work.AssignmentId),
                Comment,
                DateTimeOffset.UtcNow.AddDays(30)));

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        await UploadAsync(mentor, work.AssignmentId, Pdf("вторая версия"), "вторая.pdf");

        await StartReviewAsync(lead, work.AssignmentId);

        var response = await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(nameof(ReviewDecision.Approved), await TokenOfAsync(lead, work.AssignmentId)));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ReviewNotLatestSubmission);
    }

    /// <summary><c>REV-005</c>: one decision per version, decided by the unique index.</summary>
    [Fact]
    public async Task A_second_decision_on_one_version_is_refused()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var token = await TokenOfAsync(lead, work.AssignmentId);

        var first = await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(nameof(ReviewDecision.Approved), token));

        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(nameof(ReviewDecision.Approved), token));

        // The stale token is caught first, and that is the more useful answer: the caller's copy of the
        // assignment is out of date, which is why FE-011 offers a reload (ASN-007).
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(second)).ShouldBe(ErrorCodes.ConcurrencyConflict);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        (await context.Reviews.CountAsync(r => r.SubmissionId == work.SubmissionId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_stale_token_is_a_conflict()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(nameof(ReviewDecision.Approved), "MTIzNDU2"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ConcurrencyConflict);
    }

    // -----------------------------------------------------------------
    // Who may decide (ASN-025, TEN-006)
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("organization-admin@mentortaskflow.test")]
    [InlineData("branch-admin-head@mentortaskflow.test")]
    [InlineData("mentor-head@mentortaskflow.test")]
    public async Task Nobody_but_the_lead_decides(string email)
    {
        var work = await WorkAwaitingReviewAsync();

        using var client = await SignInAsync(email);

        var response = await client.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(nameof(ReviewDecision.Approved), "MTIzNDU2"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A Lead of another category is answered 404, not 403: the submission is outside their visibility,
    /// and any other answer would confirm it exists (<c>TEN-006</c>).
    /// </summary>
    [Fact]
    public async Task A_lead_of_another_category_cannot_see_the_submission()
    {
        var work = await WorkAwaitingReviewAsync();

        using var other = await SignInAsync("lead-python@mentortaskflow.test");

        var response = await other.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(nameof(ReviewDecision.Approved), "MTIzNDU2"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// <c>TEST-TEN-015</c>: the check is not the only guard. A direct INSERT naming a Lead of another
    /// category is refused by the database under the constraint name the specification gives it.
    /// </summary>
    [Fact]
    public async Task The_database_refuses_a_reviewer_from_another_category()
    {
        var work = await WorkAwaitingReviewAsync();
        var pythonLeadId = await IdOfAsync("lead-python@mentortaskflow.test");

        await using var connection = await postgres.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO reviews
                (id, submission_id, assignment_id, organization_id, branch_id, category_id,
                 reviewer_id, decision, comment, rework_due_at, created_at)
            VALUES
                (@id, @submissionId, @assignmentId, @organizationId, @branchId, @categoryId,
                 @reviewerId, 'Approved', NULL, NULL, now());
            """;

        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("submissionId", work.SubmissionId);
        command.Parameters.AddWithValue("assignmentId", work.AssignmentId);
        command.Parameters.AddWithValue("organizationId", _organizationId);
        command.Parameters.AddWithValue("branchId", _headOfficeId);
        command.Parameters.AddWithValue("categoryId", _sharpId);
        command.Parameters.AddWithValue("reviewerId", pythonLeadId);

        var exception = await Should.ThrowAsync<PostgresException>(command.ExecuteNonQueryAsync());

        exception.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        exception.ConstraintName.ShouldBe("fk_reviews_reviewer_scope");
    }

    /// <summary>
    /// The counterpart of the guard above, and the reason it is a trigger rather than a composite
    /// foreign key: a Lead who has reviewed work can still be moved.
    /// </summary>
    /// <remarks>
    /// Constraint 15 of 12.2a, written literally, points at the columns a transfer changes, so it would
    /// refuse to let any Lead who had ever decided anything leave their category — while a review is an
    /// immutable record that is deliberately never re-pointed (<c>REV-020</c>, <c>TEN-018</c>).
    /// </remarks>
    [Fact]
    public async Task A_lead_who_has_reviewed_can_still_be_transferred()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        (await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
                new CreateReviewRequest(nameof(ReviewDecision.Approved), await TokenOfAsync(lead, work.AssignmentId))))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        // USER-013 blocks an *active* Lead, so the role goes first — the transfer itself is what is
        // under test here.
        await DemoteToMentorAsync(admin);

        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_sharpLeadId}/change-category",
            new Contracts.Users.ChangeCategoryRequest(
                _pythonId,
                "Перевод по заявлению сотрудника",
                await TokenOfAsync(admin, _sharpLeadId)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // BRN-049: the decision stays where it was made.
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        (await context.Reviews.SingleAsync()).CategoryId.ShouldBe(_sharpId);
    }

    // -----------------------------------------------------------------
    // Reading a decision
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_mentor_reads_the_decision_on_their_own_work()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(
                nameof(ReviewDecision.NeedsRework),
                await TokenOfAsync(lead, work.AssignmentId),
                Comment,
                DateTimeOffset.UtcNow.AddDays(30)));

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        var review = await ReadAsync<ReviewDto>(
            await mentor.GetAsync($"/api/v1/submissions/{work.SubmissionId}/review"));

        review.Comment.ShouldBe(Comment);
        review.ReworkDueAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_undecided_version_has_no_review_to_read()
    {
        var work = await WorkAwaitingReviewAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        (await mentor.GetAsync($"/api/v1/submissions/{work.SubmissionId}/review"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_mentor_cannot_read_the_decision()
    {
        var work = await WorkAwaitingReviewAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        await lead.PostAsJsonAsync($"/api/v1/submissions/{work.SubmissionId}/reviews",
            new CreateReviewRequest(nameof(ReviewDecision.Approved), await TokenOfAsync(lead, work.AssignmentId)));

        using var other = await SignInAsync("mentor2-head@mentortaskflow.test");

        (await other.GetAsync($"/api/v1/submissions/{work.SubmissionId}/review"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private sealed record Work(Guid AssignmentId, Guid SubmissionId);

    /// <summary>Publishes a task, uploads a version, and — unless told otherwise — starts the review.</summary>
    private async Task<Work> WorkAwaitingReviewAsync(bool startReview = true)
    {
        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var draft = await ReadAsync<AssignmentDto>(await lead.PostAsJsonAsync("/api/v1/assignments/drafts",
            new CreateAssignmentDraftRequest(_mentorId, null, "Задача ментора", null, null)));

        var published = await ReadAsync<AssignmentDto>(await lead.PostAsJsonAsync(
            $"/api/v1/assignments/{draft.Id}/publish",
            new AssignmentActionRequest(draft.ConcurrencyToken)));

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var submission = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, published.Id, Pdf(), "работа.pdf"));

        if (startReview)
        {
            await StartReviewAsync(lead, published.Id);
        }

        return new Work(published.Id, submission.Id);
    }

    private async Task StartReviewAsync(HttpClient lead, Guid assignmentId)
    {
        var response = await lead.PostAsJsonAsync($"/api/v1/assignments/{assignmentId}/start-review",
            new AssignmentActionRequest(await TokenOfAsync(lead, assignmentId)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Turns the Lead into a Mentor so the category-change block of <c>USER-013</c> lifts.</summary>
    private async Task DemoteToMentorAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync($"/api/v1/users/{_sharpLeadId}/change-role",
            new Contracts.Users.ChangeRoleRequest(
                nameof(UserRole.Mentor),
                "Смена роли перед переводом",
                await TokenOfAsync(admin, _sharpLeadId)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<string> TokenOfAsync(HttpClient client, Guid id)
    {
        var assignment = await client.GetAsync($"/api/v1/assignments/{id}");

        if (assignment.StatusCode is HttpStatusCode.OK)
        {
            return (await ReadAsync<AssignmentDto>(assignment)).ConcurrencyToken;
        }

        return (await ReadAsync<Contracts.Users.UserDto>(await client.GetAsync($"/api/v1/users/{id}")))
            .ConcurrencyToken;
    }

    private static byte[] Pdf(string body = "содержимое работы") =>
        Encoding.UTF8.GetBytes($"%PDF-1.7\n{body}\ntrailer\n%%EOF\n");

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        Guid assignmentId,
        byte[] content,
        string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);

        return await client.PostAsync($"/api/v1/assignments/{assignmentId}/submissions", form);
    }

    private async Task<Guid> IdOfAsync(string email)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        return await context.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
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
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var passwordHash = new Pbkdf2PasswordHasher().Hash(ValidPassword);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Seeded);
        context.Branches.Add(headOffice);

        var sharp = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        var python = Category.Create(organization.Id, headOffice.Id, "Python", null, Seeded);
        context.Categories.AddRange(sharp, python);

        context.CategorySettings.AddRange(
            CategorySettings.CreateDefault(sharp, headOffice.TimeZoneId, Seeded),
            CategorySettings.CreateDefault(python, headOffice.TimeZoneId, Seeded));

        var organizationAdmin = User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded);
        var branchAdmin = User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded);
        var sharpLead = User.CreateLead(organization.Id, headOffice.Id, sharp.Id, "Лид C#", "lead-sharp@mentortaskflow.test", Seeded);
        var pythonLead = User.CreateLead(organization.Id, headOffice.Id, python.Id, "Лид Python", "lead-python@mentortaskflow.test", Seeded);
        var mentor = User.CreateMentor(organization.Id, headOffice.Id, sharp.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded);
        var second = User.CreateMentor(organization.Id, headOffice.Id, sharp.Id, "Второй ментор", "mentor2-head@mentortaskflow.test", Seeded);

        var users = new List<User> { organizationAdmin, branchAdmin, sharpLead, pythonLead, mentor, second };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        _organizationId = organization.Id;
        _headOfficeId = headOffice.Id;
        _sharpId = sharp.Id;
        _pythonId = python.Id;
        _mentorId = mentor.Id;
        _sharpLeadId = sharpLead.Id;
    }
}
