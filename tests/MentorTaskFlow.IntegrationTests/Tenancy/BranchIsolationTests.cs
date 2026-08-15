using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Reviews;
using MentorTaskFlow.Contracts.Users;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MentorTaskFlow.IntegrationTests.Tenancy;

/// <summary>
/// Cross-branch and cross-organization isolation at the API surface (TZ 31.9).
/// </summary>
/// <remarks>
/// <para>
/// Covers <c>TEST-TEN-004</c>, <c>TEST-TEN-006</c>, <c>TEST-TEN-009</c>, <c>TEST-TEN-016</c>,
/// <c>TEST-TEN-036</c>, <c>TEST-TEN-038</c> and <c>TEST-TEN-039</c> on the fixture 31.9 prescribes:
/// one organization with two branches, a category named <c>C#</c> in each, and a second organization
/// for the level-1 checks.
/// </para>
/// <para>
/// Every refusal here is 404 rather than 403 where the object exists but lies outside the caller's
/// contour. 403 would confirm that the id names something real, and a caller who can distinguish the
/// two answers can enumerate the other branch one id at a time (<c>TEN-006</c>, <c>TEN-007</c>).
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class BranchIsolationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string Zone = "Asia/Dushanbe";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;

    private Guid _organizationId;
    private Guid _headOfficeId;
    private Guid _khujandId;
    private Guid _sharpId;
    private Guid _khujandCategoryId;
    private Guid _khujandLeadId;
    private Guid _khujandMentorId;
    private Guid _khujandAssignmentId;
    private Guid _khujandSubmissionId;

    private Guid _otherOrganizationId;
    private Guid _otherCategoryId;
    private Guid _otherUserId;

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
    // Level 1 — organization (TEST-TEN-004)
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-004</c>: a filter whose value matches another organization's data returns exactly
    /// what a filter matching nothing returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Release 1.0 exposes no substring search, so the nearest thing a caller controls is a filter
    /// value. The property under test is the one 31.9 states: the two answers must be
    /// <b>indistinguishable</b>. An empty list plus a different status code, a different
    /// <c>totalCount</c>, or a different error body would each turn the filter into an oracle for
    /// «does this id exist somewhere».
    /// </para>
    /// <para>
    /// The bodies are compared whole rather than field by field, because the disclosure would be in
    /// whatever field a later change happens to add. Two fields are normalised first: <c>instance</c>
    /// echoes the requested URL and <c>traceId</c> is unique per request, so both differ between any
    /// two calls and neither says anything about what exists.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_filter_matching_another_organization_answers_exactly_as_a_filter_matching_nothing()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var foreign = await admin.GetAsync($"/api/v1/users?categoryId={_otherCategoryId}");
        var nonexistent = await admin.GetAsync($"/api/v1/users?categoryId={Guid.CreateVersion7()}");

        foreign.StatusCode.ShouldBe(nonexistent.StatusCode);
        (await NormalisedBodyAsync(foreign)).ShouldBe(await NormalisedBodyAsync(nonexistent));
    }

    /// <summary><c>TEST-TEN-004</c>, the same property for a direct lookup of a foreign user.</summary>
    [Fact]
    public async Task A_user_of_another_organization_is_indistinguishable_from_one_that_does_not_exist()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var foreign = await admin.GetAsync($"/api/v1/users/{_otherUserId}");
        var nonexistent = await admin.GetAsync($"/api/v1/users/{Guid.CreateVersion7()}");

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await NormalisedBodyAsync(foreign)).ShouldBe(await NormalisedBodyAsync(nonexistent));
    }

    // -----------------------------------------------------------------
    // Level 2 — branch (TEST-TEN-006, TEST-TEN-009)
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-006</c>: for a Branch Admin of the head office, every object of Khujand is a 404 —
    /// category, user, assignment, submission and audit record alike.
    /// </summary>
    /// <remarks>
    /// Enumerated one entity at a time on purpose. Isolation that holds for four of five entities is
    /// not isolation, and the fifth is exactly where a new endpoint forgets the scope filter.
    /// </remarks>
    [Fact]
    public async Task A_branch_admin_sees_nothing_of_the_neighbouring_branch()
    {
        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        (await admin.GetAsync($"/api/v1/categories/{_khujandCategoryId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await admin.GetAsync($"/api/v1/users/{_khujandMentorId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await admin.GetAsync($"/api/v1/assignments/{_khujandAssignmentId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // TEST-TEN-017 in kind: the URL is not issued and the storage key is not disclosed. The key
        // is checked by its distinguishing part — the assignment id — because the literal prefix
        // `submissions/` also occurs in the request path the error echoes back.
        var download = await admin.GetAsync($"/api/v1/submissions/{_khujandSubmissionId}/download-url");
        download.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await download.Content.ReadAsStringAsync()).ShouldNotContain(_khujandAssignmentId.ToString("N"));

        // TEST-TEN-018 in kind: the neighbouring branch's audit trail is not readable either.
        var auditLog = await admin.GetAsync("/api/v1/admin/audit-log?pageSize=100");
        auditLog.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await auditLog.Content.ReadAsStringAsync()).ShouldNotContain(_khujandMentorId.ToString());
    }

    /// <summary><c>TEST-TEN-009</c>: a Mentor of the head office cannot read Khujand's assignment.</summary>
    [Fact]
    public async Task A_mentor_cannot_read_an_assignment_of_another_branch()
    {
        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        var response = await mentor.GetAsync($"/api/v1/assignments/{_khujandAssignmentId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ResourceNotFound);
    }

    // -----------------------------------------------------------------
    // TEST-TEN-016 — review across a branch boundary
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-016</c>: a Lead of Khujand cannot review a submission of the head office — refused
    /// by visibility at the API, and by the database when the API is bypassed.
    /// </summary>
    /// <remarks>
    /// Both halves are required by <c>TEN-099</c>, and they prove different things. The 404 shows the
    /// application applies scope; the constraint violation shows isolation does not <b>depend</b> on
    /// the application being correct.
    /// </remarks>
    [Fact]
    public async Task A_lead_cannot_review_a_submission_of_another_branch()
    {
        var (assignmentId, submissionId) = await SeedWorkAsync(
            _headOfficeId, _sharpId, await IdOfAsync("mentor-head@mentortaskflow.test"), await IdOfAsync("lead-sharp@mentortaskflow.test"));

        using var khujandLead = await SignInAsync("lead-khujand@mentortaskflow.test");

        var response = await khujandLead.PostAsJsonAsync(
            $"/api/v1/submissions/{submissionId}/reviews",
            new CreateReviewRequest("Approved", "token-placeholder", "Выглядит хорошо, принимаю работу."));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Reviews.CountAsync()).ShouldBe(0);

        // The database refuses the same thing, under the application's own role.
        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertReviewAsync(submissionId, assignmentId, _khujandLeadId));

        exception.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        exception.ConstraintName.ShouldBe("fk_reviews_reviewer_scope");
    }

    // -----------------------------------------------------------------
    // TEST-TEN-036 — all-branches mode is read-only
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-036</c>: a mutation with no branch chosen is refused even when the UI would not
    /// have offered the button.
    /// </summary>
    /// <remarks>
    /// The frontend half of the case — a disabled control — is out of scope here; this is the half
    /// that matters for isolation, because a client is not a security boundary. All-branches is a
    /// <b>read</b> context (<c>TEN-034</c>): no endpoint changes more than one branch per request, so
    /// there is no branch to assume and nothing sensible to default to.
    /// </remarks>
    [Fact]
    public async Task A_mutation_without_a_chosen_branch_is_refused()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync(
            "/api/v1/users",
            new CreateUserRequest("Новый Ментор", "new-mentor@mentortaskflow.test", "Mentor", null, _sharpId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.BranchContextRequired);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Users.AnyAsync(u => u.Email == "new-mentor@mentortaskflow.test")).ShouldBeFalse();
    }

    // -----------------------------------------------------------------
    // TEST-TEN-038, TEST-TEN-039 — privilege and scope in the body
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-038</c>: a Branch Admin cannot mint an Organization Admin.
    /// </summary>
    /// <remarks>
    /// The refusal is 403 and not 404: the caller may create users, and the endpoint is theirs — it is
    /// the <b>privilege</b> being asked for that is outside their contour (TZ 9.3). Escalation of this
    /// shape is the one path from one branch to the whole organization, so it is also audited.
    /// </remarks>
    [Fact]
    public async Task A_branch_admin_cannot_create_an_organization_admin()
    {
        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await admin.PostAsJsonAsync(
            "/api/v1/users",
            new CreateUserRequest("Самозванец", "impostor@mentortaskflow.test", "Admin", "Organization", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Users.AnyAsync(u => u.Email == "impostor@mentortaskflow.test")).ShouldBeFalse();
    }

    /// <summary>
    /// <c>TEST-TEN-039</c>: a Lead cannot place a mentor outside their own category, however the
    /// request is phrased.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case has two halves and they end differently, as 31.9 says. <c>branchId</c> is a field no
    /// role may send (<c>SEC-003</c>), so the payload is rejected as malformed before any
    /// authorization question arises — 400 <c>VALIDATION_FAILED</c>.
    /// </para>
    /// <para>
    /// A <c>categoryId</c> naming the neighbouring branch is not an error but is <b>not honoured</b>
    /// either: a Lead has exactly one category, so the field is taken from the Lead rather than from
    /// the request, and the mentor is created in the Lead's own category. That is the second half of
    /// the case verbatim — «при обходе валидации Mentor создаётся строго в категории Lead» — and it is
    /// the safer of the two designs, because there is no path by which a Lead's request can place a
    /// person anywhere but their own team.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_lead_cannot_place_a_mentor_in_another_branch()
    {
        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        // branchId is refused outright: unknown member, malformed payload (SEC-003, API-005).
        var withBranch = await lead.PostAsync(
            "/api/v1/users",
            JsonContent.Create(new
            {
                fullName = "Чужой Ментор",
                email = "foreign-mentor@mentortaskflow.test",
                role = "Mentor",
                categoryId = _khujandCategoryId,
                branchId = _khujandId,
            }));

        withBranch.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(withBranch)).ShouldBe(ErrorCodes.ValidationFailed);

        // The neighbouring branch's category, named through the field the contract does allow.
        var byCategory = await lead.PostAsJsonAsync(
            "/api/v1/users",
            new CreateUserRequest("Свой Ментор", "own-mentor@mentortaskflow.test", "Mentor", null, _khujandCategoryId));

        byCategory.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReadAsync<UserDto>(byCategory)).CategoryId.ShouldBe(_sharpId);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        (await context.Users.AnyAsync(u => u.Email == "foreign-mentor@mentortaskflow.test")).ShouldBeFalse();

        // The one that was created landed in the head office, never in Khujand.
        var created = await context.Users.SingleAsync(u => u.Email == "own-mentor@mentortaskflow.test");
        created.BranchId.ShouldBe(_headOfficeId);
        created.CategoryId.ShouldBe(_sharpId);
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    /// <summary>Inserts a review directly, so the database is the only thing that can refuse it.</summary>
    private async Task InsertReviewAsync(Guid submissionId, Guid assignmentId, Guid reviewerId)
    {
        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO reviews (id, submission_id, assignment_id, organization_id, branch_id,
                                 category_id, reviewer_id, decision, comment, rework_due_at, created_at)
            VALUES (@id, @submission, @assignment, @organization, @branch,
                    @category, @reviewer, 'Approved', NULL, NULL, now());
            """;

        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("submission", submissionId);
        command.Parameters.AddWithValue("assignment", assignmentId);
        command.Parameters.AddWithValue("organization", _organizationId);
        command.Parameters.AddWithValue("branch", _headOfficeId);
        command.Parameters.AddWithValue("category", _sharpId);
        command.Parameters.AddWithValue("reviewer", reviewerId);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<(Guid AssignmentId, Guid SubmissionId)> SeedWorkAsync(
        Guid branchId,
        Guid categoryId,
        Guid mentorId,
        Guid leadId)
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var assignment = Assignment.CreateDraft(
            _organizationId, branchId, categoryId, mentorId, leadId, null,
            "Задача", null, Seeded.AddDays(3), Seeded.AddMinutes(-5));

        assignment.Publish(leadId, Seeded);
        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();

        var submittedAt = Seeded.AddHours(2);

        var submission = Submission.Record(
            Guid.CreateVersion7(), assignment, 1,
            $"submissions/{assignment.Id:N}-1.pdf", "работа.pdf",
            FileExtension.Pdf, 1024, new string('a', 64), false, mentorId, submittedAt);

        context.Submissions.Add(submission);
        assignment.Submit(isFirstVersion: true, submittedAt);
        assignment.StartReview(submittedAt.AddMinutes(30));

        await context.SaveChangesAsync();

        return (assignment.Id, submission.Id);
    }

    private async Task<Guid> IdOfAsync(string email)
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        return (await context.Users.AsNoTracking().SingleAsync(u => u.Email == email)).Id;
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

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// The response body with the two per-request fields blanked out.
    /// </summary>
    /// <remarks>
    /// <c>instance</c> is the URL that was asked for and <c>traceId</c> is issued per request, so both
    /// differ between any two calls whatever the answer. Everything else must match, and comparing
    /// the rest whole is what makes the check survive a field being added later.
    /// </remarks>
    private static async Task<string> NormalisedBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (body.Length == 0)
        {
            return body;
        }

        using var document = JsonDocument.Parse(body);

        var properties = document.RootElement.ValueKind is JsonValueKind.Object
            ? document.RootElement.EnumerateObject()
                .Select(p => p.Name is "instance" or "traceId" ? $"{p.Name}=<per-request>" : $"{p.Name}={p.Value.GetRawText()}")
            : [body];

        return string.Join('\n', properties);
    }

    private async Task SeedAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var passwordHash = new Pbkdf2PasswordHasher().Hash(ValidPassword);

        // The fixture of 31.9: one organization, two branches, C# in each.
        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        var other = Organization.Provision("Other Academy", "other-academy", Seeded);
        context.Organizations.AddRange(organization, other);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, Zone, Seeded);
        var khujand = Branch.Create(organization.Id, "Филиал Худжанд", "KHJ", null, Zone, Seeded);
        var otherBranch = Branch.CreateHeadOffice(other.Id, "Головной офис", "OTH", null, Zone, Seeded);
        context.Branches.AddRange(headOffice, khujand, otherBranch);

        var sharp = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        var khujandCategory = Category.Create(organization.Id, khujand.Id, "C#", null, Seeded);
        var otherCategory = Category.Create(other.Id, otherBranch.Id, "C#", null, Seeded);
        context.Categories.AddRange(sharp, khujandCategory, otherCategory);

        foreach (var (category, branch) in new[]
                 {
                     (sharp, headOffice), (khujandCategory, khujand), (otherCategory, otherBranch),
                 })
        {
            context.CategorySettings.Add(CategorySettings.CreateDefault(category, branch.TimeZoneId, Seeded));
        }

        var users = new List<User>
        {
            User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded),
            User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded),
            User.CreateLead(organization.Id, headOffice.Id, sharp.Id, "Лид C#", "lead-sharp@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, headOffice.Id, sharp.Id, "Ментор HQ", "mentor-head@mentortaskflow.test", Seeded),
            User.CreateLead(organization.Id, khujand.Id, khujandCategory.Id, "Лид Худжанда", "lead-khujand@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, khujand.Id, khujandCategory.Id, "Ментор Худжанда", "mentor-khujand@mentortaskflow.test", Seeded),
            User.CreateMentor(other.Id, otherBranch.Id, otherCategory.Id, "Ментор Чужой", "mentor-other@othertaskflow.test", Seeded),
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
        _sharpId = sharp.Id;
        _khujandCategoryId = khujandCategory.Id;
        _khujandLeadId = users.Single(u => u.Email == "lead-khujand@mentortaskflow.test").Id;
        _khujandMentorId = users.Single(u => u.Email == "mentor-khujand@mentortaskflow.test").Id;

        _otherOrganizationId = other.Id;
        _otherCategoryId = otherCategory.Id;
        _otherUserId = users.Single(u => u.Email == "mentor-other@othertaskflow.test").Id;

        var work = await SeedWorkAsync(_khujandId, _khujandCategoryId, _khujandMentorId, _khujandLeadId);
        _khujandAssignmentId = work.AssignmentId;
        _khujandSubmissionId = work.SubmissionId;

        await SeedKhujandAuditAsync();
    }

    /// <summary>An audit row that exists only in Khujand, so a leak into HQ's journal is visible.</summary>
    private async Task SeedKhujandAuditAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        context.AuditLogs.Add(AuditLog.Record(
            AuditActions.UserUpdate,
            nameof(User),
            _organizationId,
            _khujandId,
            _khujandCategoryId,
            _khujandMentorId,
            AuditActorType.User,
            _khujandLeadId,
            UserRole.Lead,
            actorAdminScope: null,
            AuditResult.Success,
            Guid.CreateVersion7(),
            Seeded));

        await context.SaveChangesAsync();
    }
}
