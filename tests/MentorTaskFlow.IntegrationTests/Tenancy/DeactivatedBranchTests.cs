using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Assignments;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Categories;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Reviews;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.IntegrationTests.Tenancy;

/// <summary>
/// A deactivated branch: frozen, not deleted (TZ 39.4, <c>BRN-031</c>, <c>BRN-032</c>).
/// </summary>
/// <remarks>
/// <para>
/// Covers <c>TEST-TEN-023</c> — every business mutation in the branch answers 403
/// <c>BRANCH_INACTIVE</c> — and <c>TEST-TEN-024</c> — the branch's own users still sign in and still
/// read their history.
/// </para>
/// <para>
/// The two cases are the two halves of one decision. Deactivation exists so a branch that has stopped
/// operating can stop producing work without its records becoming unreachable: a dispute about a task
/// graded last term is settled from those records, and deleting or hiding them would settle it in
/// favour of whoever remembers it best.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DeactivatedBranchTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string Zone = "Asia/Dushanbe";
    private const string BranchHeader = "X-MTF-Branch-Id";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;

    private Guid _organizationId;
    private Guid _headOfficeId;
    private Guid _khujandId;
    private Guid _khujandCategoryId;
    private Guid _khujandLeadId;
    private Guid _khujandMentorId;
    private Guid _liveAssignmentId;
    private Guid _submissionId;

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
    // TEST-TEN-023 — every mutation is refused
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-023</c>: creating a category in a deactivated branch is 403 <c>BRANCH_INACTIVE</c>.
    /// </summary>
    [Fact]
    public async Task A_category_cannot_be_created_in_a_deactivated_branch()
    {
        await DeactivateKhujandAsync();

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");
        admin.DefaultRequestHeaders.Add(BranchHeader, _khujandId.ToString());

        var response = await admin.PostAsJsonAsync(
            "/api/v1/categories",
            new CreateCategoryRequest("Go", null));

        await ShouldBeBranchInactiveAsync(response);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Categories.CountAsync(c => c.BranchId == _khujandId)).ShouldBe(1);
    }

    /// <summary><c>TEST-TEN-023</c>: nothing new is published either.</summary>
    [Fact]
    public async Task An_assignment_cannot_be_created_in_a_deactivated_branch()
    {
        await DeactivateKhujandAsync();

        using var lead = await SignInAsync("lead-khujand@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync(
            "/api/v1/assignments/drafts",
            new CreateAssignmentDraftRequest(_khujandMentorId, null, "Новая задача", null, Seeded.AddDays(5)));

        await ShouldBeBranchInactiveAsync(response);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Assignments.CountAsync(a => a.BranchId == _khujandId)).ShouldBe(1);
    }

    /// <summary>
    /// <c>TEST-TEN-023</c>: an upload is refused, and storage is never reached.
    /// </summary>
    /// <remarks>
    /// The order is what the case is about — «storage не затронут». The branch check runs before the
    /// file is read, so a refusal here means no object was written, no key was allocated and no
    /// presigned URL existed to leak. The absence of a submission row is the observable half of that.
    /// </remarks>
    [Fact]
    public async Task A_submission_cannot_be_uploaded_into_a_deactivated_branch()
    {
        await DeactivateKhujandAsync();

        using var mentor = await SignInAsync("mentor-khujand@mentortaskflow.test");

        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent("%PDF-1.7 ..."u8.ToArray());
        file.Headers.ContentType = new("application/pdf");
        content.Add(file, "file", "работа.pdf");

        var response = await mentor.PostAsync($"/api/v1/assignments/{_liveAssignmentId}/submissions", content);

        await ShouldBeBranchInactiveAsync(response);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Submissions.CountAsync(s => s.BranchId == _khujandId)).ShouldBe(1);
    }

    /// <summary><c>TEST-TEN-023</c>: work already submitted cannot be reviewed while the branch is frozen.</summary>
    [Fact]
    public async Task A_review_cannot_be_created_in_a_deactivated_branch()
    {
        await DeactivateKhujandAsync();

        using var lead = await SignInAsync("lead-khujand@mentortaskflow.test");

        var assignment = await lead.GetAsync($"/api/v1/assignments/{_liveAssignmentId}");
        using var document = JsonDocument.Parse(await assignment.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("concurrencyToken").GetString()!;

        var response = await lead.PostAsJsonAsync(
            $"/api/v1/submissions/{_submissionId}/reviews",
            new CreateReviewRequest("Approved", token, "Принимаю работу без замечаний."));

        await ShouldBeBranchInactiveAsync(response);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Reviews.CountAsync()).ShouldBe(0);
    }

    // -----------------------------------------------------------------
    // TEST-TEN-024 — the records stay readable
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-024</c>: the branch's own people sign in and read their history (<c>BRN-031</c>).
    /// </summary>
    /// <remarks>
    /// Login is asserted separately from reading because they fail for different reasons and only one
    /// of them would be noticed. A deactivated branch that blocked authentication would look like a
    /// forgotten password to everyone in it.
    /// </remarks>
    [Fact]
    public async Task The_people_of_a_deactivated_branch_still_sign_in_and_read_their_history()
    {
        await DeactivateKhujandAsync();

        using var mentor = await SignInAsync("mentor-khujand@mentortaskflow.test");

        var assignment = await mentor.GetAsync($"/api/v1/assignments/{_liveAssignmentId}");
        assignment.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await mentor.GetAsync($"/api/v1/assignments/{_liveAssignmentId}/history"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await mentor.GetAsync($"/api/v1/assignments/{_liveAssignmentId}/submissions"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary><c>TEST-TEN-024</c>: the Organization Admin reads the frozen branch too.</summary>
    [Fact]
    public async Task An_organization_admin_still_reads_a_deactivated_branch()
    {
        await DeactivateKhujandAsync();

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");
        admin.DefaultRequestHeaders.Add(BranchHeader, _khujandId.ToString());

        (await admin.GetAsync($"/api/v1/assignments/{_liveAssignmentId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/v1/categories/{_khujandCategoryId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/v1/users/{_khujandMentorId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// <c>TEST-TEN-024</c>: the neighbouring branch is untouched.
    /// </summary>
    /// <remarks>
    /// Deactivation is one branch's state, and the guard is keyed on the branch of the object being
    /// written. A guard that read the request's branch context instead would freeze whichever branch
    /// the acting Organization Admin happened to have selected.
    /// </remarks>
    [Fact]
    public async Task The_neighbouring_branch_keeps_working()
    {
        await DeactivateKhujandAsync();

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");
        admin.DefaultRequestHeaders.Add(BranchHeader, _headOfficeId.ToString());

        var response = await admin.PostAsJsonAsync("/api/v1/categories", new CreateCategoryRequest("Go", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    private static async Task ShouldBeBranchInactiveAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("code").GetString().ShouldBe(ErrorCodes.BranchInactive);
    }

    private async Task DeactivateKhujandAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        (await context.Branches.SingleAsync(b => b.Id == _khujandId)).Deactivate(Seeded.AddDays(1));

        await context.SaveChangesAsync();
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ValidPassword));

        // TEST-TEN-024: authentication is unaffected by the branch being frozen.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var login = JsonSerializer.Deserialize<LoginResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);

        return client;
    }

    private async Task SeedAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var passwordHash = new Pbkdf2PasswordHasher().Hash(ValidPassword);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, Zone, Seeded);
        var khujand = Branch.Create(organization.Id, "Филиал Худжанд", "KHJ", null, Zone, Seeded);
        context.Branches.AddRange(headOffice, khujand);

        var sharp = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        var khujandCategory = Category.Create(organization.Id, khujand.Id, "C#", null, Seeded);
        context.Categories.AddRange(sharp, khujandCategory);

        context.CategorySettings.AddRange(
            CategorySettings.CreateDefault(sharp, Zone, Seeded),
            CategorySettings.CreateDefault(khujandCategory, Zone, Seeded));

        var users = new List<User>
        {
            User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded),
            User.CreateLead(organization.Id, khujand.Id, khujandCategory.Id, "Лид Худжанда", "lead-khujand@mentortaskflow.test", Seeded),
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
        _khujandCategoryId = khujandCategory.Id;
        _khujandLeadId = users.Single(u => u.Email == "lead-khujand@mentortaskflow.test").Id;
        _khujandMentorId = users.Single(u => u.Email == "mentor-khujand@mentortaskflow.test").Id;

        await SeedWorkAsync();
    }

    /// <summary>One assignment with one submission, so the branch has a history worth reading.</summary>
    private async Task SeedWorkAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var assignment = Assignment.CreateDraft(
            _organizationId, _khujandId, _khujandCategoryId, _khujandMentorId, _khujandLeadId, null,
            "Задача Худжанда", null, Seeded.AddDays(3), Seeded.AddMinutes(-5));

        assignment.Publish(_khujandLeadId, Seeded);
        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();

        var submittedAt = Seeded.AddHours(2);

        var submission = Submission.Record(
            Guid.CreateVersion7(), assignment, 1,
            $"submissions/{assignment.Id:N}-1.pdf", "работа.pdf",
            FileExtension.Pdf, 1024, new string('a', 64), false, _khujandMentorId, submittedAt);

        context.Submissions.Add(submission);
        assignment.Submit(isFirstVersion: true, submittedAt);
        assignment.StartReview(submittedAt.AddMinutes(30));

        await context.SaveChangesAsync();

        _liveAssignmentId = assignment.Id;
        _submissionId = submission.Id;
    }
}
