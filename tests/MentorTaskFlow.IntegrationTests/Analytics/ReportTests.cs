using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Analytics;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Reviews;
using MentorTaskFlow.Domain.Schedule;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.IntegrationTests.Analytics;

/// <summary>Report metrics, their scope and the privacy threshold (TZ 21).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReportTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string Zone = "Asia/Dushanbe";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Everything is placed inside September so the default window never straddles it.</summary>
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 30);
    private static readonly DateTimeOffset AssignedAt = new(2026, 9, 2, 6, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _organizationId;
    private Guid _headOfficeId;
    private Guid _khujandId;
    private Guid _sharpId;
    private Guid _khujandCategoryId;
    private Guid _leadId;
    private readonly List<Guid> _mentorIds = [];
    private Guid _khujandMentorId;

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

    // -----------------------------------------------------------------
    // Metrics (21.2, 21.3, 21.4)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_completed_assignment_is_counted_and_timed()
    {
        await CompleteAsync(_mentorIds[0], versions: 1, approved: true);

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await lead.GetAsync(TeamUrl()));

        report.Total.TotalAssignments.ShouldBe(1);
        report.Total.ApprovedAssignments.ShouldBe(1);

        // One submission and an approving review on it: first pass (21.3, all three conditions).
        report.Total.FirstPassApprovalRate.ShouldBe(100d);

        // Submitted two hours after assignment, approved six hours after that.
        report.Total.InitialSubmissionTime.MedianHours.ShouldBe(2d);
        report.Total.TotalCycleTime.MedianHours.ShouldBe(8d);
    }

    /// <summary>
    /// 21.3: a second version disqualifies the task from first-pass, even though it was approved.
    /// </summary>
    [Fact]
    public async Task A_task_approved_on_the_second_version_is_not_first_pass()
    {
        await CompleteAsync(_mentorIds[0], versions: 2, approved: true);

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await lead.GetAsync(TeamUrl()));

        report.Total.ApprovedAssignments.ShouldBe(1);
        report.Total.FirstPassApprovalRate.ShouldBe(0d);
        report.Total.AverageVersions.ShouldBe(2d);
    }

    /// <summary>
    /// <c>ANA-004</c>: an empty denominator yields null, not zero. Zero percent would read as «the
    /// team approved nothing», which is a different and wrong statement.
    /// </summary>
    [Fact]
    public async Task An_empty_denominator_yields_null()
    {
        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await lead.GetAsync(TeamUrl()));

        report.Total.TotalAssignments.ShouldBe(0);
        report.Total.FirstPassApprovalRate.ShouldBeNull();
        report.Total.OverdueRate.ShouldBeNull();
        report.Total.LateSubmissionRate.ShouldBeNull();
        report.Total.AverageVersions.ShouldBeNull();
        report.Total.TotalCycleTime.MedianHours.ShouldBeNull();
    }

    /// <summary>
    /// <c>TEST-ANA-003</c>: the two formulations of <c>OverdueRate</c> must agree. Counting
    /// <c>MarkedOverdue</c> events instead of assignments would exceed 100% for a task that slipped
    /// twice.
    /// </summary>
    [Fact]
    public async Task Both_formulations_of_the_overdue_rate_agree()
    {
        // Published and left alone: MarkOverdue is only reachable from Assigned or NeedsRework (14.4).
        var assignmentId = await PublishOnlyAsync(_mentorIds[0]);
        await MarkOverdueTwiceAsync(assignmentId);

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await lead.GetAsync(TeamUrl()));

        report.Total.TotalAssignments.ShouldBe(1);
        report.Total.OverdueRate.ShouldBe(100d);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var byEvents = await context.TaskEvents
            .Where(e => e.EventType == TaskEventType.MarkedOverdue)
            .Select(e => e.AssignmentId)
            .Distinct()
            .CountAsync();

        var byField = await context.Assignments.CountAsync(a => a.OverdueAt != null);

        // Two events, one assignment — and the rate follows the assignment count.
        (await context.TaskEvents.CountAsync(e => e.EventType == TaskEventType.MarkedOverdue)).ShouldBe(2);
        byEvents.ShouldBe(byField);
    }

    [Fact]
    public async Task Late_submissions_are_rated_over_submissions_not_assignments()
    {
        await CompleteAsync(_mentorIds[0], versions: 2, approved: true, lateFirstVersion: true);

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await lead.GetAsync(TeamUrl()));

        // One of two submissions was late.
        report.Total.LateSubmissionRate.ShouldBe(50d);
    }

    /// <summary><c>ANA-001</c>: drafts and suggestions are never work, whatever the filters say.</summary>
    [Fact]
    public async Task Drafts_and_suggestions_are_never_counted()
    {
        await SeedDraftAndSuggestionAsync();

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var report = await ReadAsync<ReportDto>(await lead.GetAsync(TeamUrl() + "&includeCancelled=true"));

        report.Total.TotalAssignments.ShouldBe(0);
    }

    // -----------------------------------------------------------------
    // Period (ANA-002, ANA-006, TEN-074)
    // -----------------------------------------------------------------

    [Fact]
    public async Task The_period_is_bounded_by_the_branch_zone()
    {
        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await lead.GetAsync(TeamUrl()));

        report.PeriodTimeZoneId.ShouldBe(Zone);
        report.From.ShouldBe(From);
        report.To.ShouldBe(To);
    }

    /// <summary>
    /// <c>ANA-002</c>: the interval is half-open, so an assignment on the last day is included and one
    /// on the first day of the next period is not.
    /// </summary>
    [Fact]
    public async Task The_last_day_of_the_period_is_included()
    {
        await CompleteAsync(_mentorIds[0], versions: 1, approved: false, assignedAt: LocalMidnightUtc(To).AddHours(23));
        await CompleteAsync(_mentorIds[1], versions: 1, approved: false, assignedAt: LocalMidnightUtc(To.AddDays(1)));

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await lead.GetAsync(TeamUrl()));

        report.Total.TotalAssignments.ShouldBe(1);
    }

    [Fact]
    public async Task An_unfinished_period_is_flagged()
    {
        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5).Date);
        var url = $"/api/v1/reports/team?from={today.AddDays(-7):yyyy-MM-dd}&to={today:yyyy-MM-dd}";

        (await ReadAsync<ReportDto>(await lead.GetAsync(url))).IsPartialPeriod.ShouldBeTrue();
    }

    [Fact]
    public async Task A_reversed_period_is_refused()
    {
        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var response = await lead.GetAsync("/api/v1/reports/team?from=2026-09-30&to=2026-09-01");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------
    // Scope and grouping (TEN-070, TEN-071, TEN-073)
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-010</c>: identically named categories of two branches are different things and must
    /// not merge. Grouping by name would combine two unrelated study streams.
    /// </summary>
    [Fact]
    public async Task Identically_named_categories_of_two_branches_stay_apart()
    {
        await CompleteAsync(_mentorIds[0], versions: 1, approved: false);
        await CompleteInKhujandAsync();

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await admin.GetAsync(TeamUrl()));

        report.Rows.Count.ShouldBe(2);
        report.Rows.Select(r => r.CategoryName).Distinct().ShouldHaveSingleItem().ShouldBe("C#");
        report.Rows.Select(r => r.CategoryId).Distinct().Count().ShouldBe(2);

        // TEN-073: every row of a cross-branch report names its branch.
        report.Rows.ShouldAllBe(r => r.Branch != null);
        report.IsCrossBranchAggregate.ShouldBeTrue();
        report.IncludedBranchIds.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_branch_admin_sees_only_their_own_branch()
    {
        await CompleteAsync(_mentorIds[0], versions: 1, approved: false);
        await CompleteInKhujandAsync();

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await admin.GetAsync(TeamUrl()));

        report.Rows.ShouldAllBe(r => r.Branch!.Id == _headOfficeId);
        report.IsCrossBranchAggregate.ShouldBeFalse();
        report.Total.TotalAssignments.ShouldBe(1);
    }

    /// <summary>A branch id in the query is not the Branch Admin's to choose (<c>TEN-070</c>).</summary>
    [Fact]
    public async Task A_branch_admin_cannot_ask_for_another_branch()
    {
        await CompleteInKhujandAsync();

        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await admin.GetAsync($"{TeamUrl()}&branchId={_khujandId}"));

        report.Total.TotalAssignments.ShouldBe(0);
    }

    /// <summary><c>ANA-011</c>: a Lead sees each mentor of their category by name.</summary>
    [Fact]
    public async Task A_lead_sees_a_breakdown_by_mentor()
    {
        await CompleteAsync(_mentorIds[0], versions: 1, approved: false);
        await CompleteAsync(_mentorIds[1], versions: 1, approved: false);

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await lead.GetAsync(TeamUrl()));

        report.Rows.Count.ShouldBe(2);
        report.Rows.ShouldAllBe(r => r.MentorId != null && r.MentorFullName != null);
    }

    [Fact]
    public async Task Only_an_organization_admin_may_compare_branches()
    {
        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        (await admin.GetAsync("/api/v1/reports/branches")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_branch_comparison_groups_by_branch()
    {
        await CompleteAsync(_mentorIds[0], versions: 1, approved: false);
        await CompleteInKhujandAsync();

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await admin.GetAsync(BranchesUrl()));

        report.Rows.Count.ShouldBe(2);
        report.Rows.ShouldAllBe(r => r.CategoryId == null);
        report.IsCrossBranchAggregate.ShouldBeTrue();
    }

    // -----------------------------------------------------------------
    // Privacy (ANA-012, ANA-013, TEN-072)
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-005</c>: the threshold applies inside the requested scope. Four mentors in the
    /// identically named category of another branch do not help.
    /// </summary>
    [Fact]
    public async Task A_mentor_is_refused_a_team_report_below_five_mentors()
    {
        using var mentor = await SignInAsync("mentor1-head@mentortaskflow.test");

        var response = await mentor.GetAsync(TeamUrl());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.InsufficientSampleSize);

        // No partial data of any kind: «team of five minus team of four» is the attack the
        // whole-or-nothing rule closes.
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("totalAssignments");
    }

    [Fact]
    public async Task A_mentor_receives_the_team_report_once_five_mentors_have_worked()
    {
        foreach (var mentorId in _mentorIds)
        {
            await CompleteAsync(mentorId, versions: 1, approved: false);
        }

        using var mentor = await SignInAsync("mentor1-head@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await mentor.GetAsync(TeamUrl()));

        report.Total.TotalAssignments.ShouldBe(5);

        // Anonymised: no breakdown at all, so no row can be attributed to a person.
        report.Rows.ShouldBeEmpty();
    }

    /// <summary><c>ANA-013</c>: a Mentor naming somebody else is a 400, not a narrowed answer.</summary>
    [Fact]
    public async Task A_mentor_cannot_ask_for_another_persons_report()
    {
        using var mentor = await SignInAsync("mentor1-head@mentortaskflow.test");

        var response = await mentor.GetAsync($"/api/v1/reports/personal?mentorId={_mentorIds[1]}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_mentor_receives_their_own_report_without_naming_themselves()
    {
        await CompleteAsync(_mentorIds[0], versions: 1, approved: true);

        using var mentor = await SignInAsync("mentor1-head@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await mentor.GetAsync(PersonalUrl()));

        report.Total.TotalAssignments.ShouldBe(1);
        report.Total.ApprovedAssignments.ShouldBe(1);
    }

    /// <summary><c>ANA-014</c>: the personal report breaks down by category.</summary>
    [Fact]
    public async Task A_personal_report_is_broken_down_by_category()
    {
        await CompleteAsync(_mentorIds[0], versions: 1, approved: false);

        using var mentor = await SignInAsync("mentor1-head@mentortaskflow.test");
        var report = await ReadAsync<ReportDto>(await mentor.GetAsync(PersonalUrl()));

        report.Rows.ShouldHaveSingleItem().CategoryId.ShouldBe(_sharpId);
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    private static string TeamUrl() => $"/api/v1/reports/team?from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}";

    private static string PersonalUrl() => $"/api/v1/reports/personal?from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}";

    private static string BranchesUrl() => $"/api/v1/reports/branches?from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}";

    /// <summary>Midnight of a local date in the reporting zone, expressed in UTC (UTC+5).</summary>
    private static DateTimeOffset LocalMidnightUtc(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5)).ToUniversalTime();

    /// <summary>Drives one assignment through the cycle so the metrics have something to measure.</summary>
    private async Task<Guid> CompleteAsync(
        Guid mentorId,
        int versions,
        bool approved,
        bool lateFirstVersion = false,
        DateTimeOffset? assignedAt = null)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var start = assignedAt ?? AssignedAt;

        var assignment = Assignment.CreateDraft(
            _organizationId, _headOfficeId, _sharpId, mentorId, _leadId, null,
            "Задача", null, start.AddDays(3), start.AddMinutes(-5));

        assignment.Publish(_leadId, start);
        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();

        for (var version = 1; version <= versions; version++)
        {
            var submittedAt = start.AddHours(2 * version);

            var submission = Submission.Record(
                Guid.CreateVersion7(),
                assignment,
                version,
                $"submissions/key-{assignment.Id:N}-{version}.pdf",
                "работа.pdf",
                FileExtension.Pdf,
                1024,
                new string((char)('a' + version), 64),
                lateFirstVersion && version == 1,
                mentorId,
                submittedAt);

            context.Submissions.Add(submission);

            assignment.Submit(isFirstVersion: version == 1, submittedAt);
            assignment.StartReview(submittedAt.AddMinutes(30));

            var isLast = version == versions;
            var reviewedAt = start.AddHours(8);

            if (isLast && approved)
            {
                context.Reviews.Add(Review.Approve(submission, _leadId, null, reviewedAt));
                assignment.Approve(reviewedAt);
            }
            else if (!isLast)
            {
                var rework = start.AddHours(2 * version + 1);
                context.Reviews.Add(Review.RequestRework(
                    submission, _leadId, "Переделайте раздел про индексы.", start.AddDays(10), rework));

                assignment.RequestRework(start.AddDays(10), rework);
            }

            await context.SaveChangesAsync();
        }

        return assignment.Id;
    }

    /// <summary>Publishes a task and stops there, so it stays in <c>Assigned</c>.</summary>
    private async Task<Guid> PublishOnlyAsync(Guid mentorId)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var assignment = Assignment.CreateDraft(
            _organizationId, _headOfficeId, _sharpId, mentorId, _leadId, null,
            "Задача", null, AssignedAt.AddDays(3), AssignedAt.AddMinutes(-5));

        assignment.Publish(_leadId, AssignedAt);

        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();

        return assignment.Id;
    }

    private async Task CompleteInKhujandAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var lead = await context.Users.FirstAsync(u => u.Email == "lead-khujand@mentortaskflow.test");

        var assignment = Assignment.CreateDraft(
            _organizationId, _khujandId, _khujandCategoryId, _khujandMentorId, lead.Id, null,
            "Задача Худжанда", null, AssignedAt.AddDays(3), AssignedAt.AddMinutes(-5));

        assignment.Publish(lead.Id, AssignedAt);

        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();
    }

    private async Task MarkOverdueTwiceAsync(Guid assignmentId)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var assignment = await context.Assignments.SingleAsync(a => a.Id == assignmentId);
        var first = AssignedAt.AddDays(4);

        assignment.MarkOverdue(first);
        context.TaskEvents.Add(TaskEvent.Record(
            assignment, TaskEventType.MarkedOverdue, null, AssignmentStatus.Assigned,
            AssignmentStatus.Overdue, Guid.CreateVersion7(), first));

        await context.SaveChangesAsync();

        // A second slip against a later deadline: another event, the same assignment, and OverdueAt
        // keeps the first moment (14.4).
        var second = AssignedAt.AddDays(12);
        context.TaskEvents.Add(TaskEvent.Record(
            assignment, TaskEventType.MarkedOverdue, null, AssignmentStatus.NeedsRework,
            AssignmentStatus.Overdue, Guid.CreateVersion7(), second));

        await context.SaveChangesAsync();
    }

    private async Task SeedDraftAndSuggestionAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var topic = Topic.Create(_organizationId, _headOfficeId, _sharpId, 1, null, "Введение", null, Seeded);
        context.Topics.Add(topic);

        var template = TopicAssignment.Create(topic, TopicAssignmentType.HomeTask, "Домашнее задание", null, true, Seeded);
        context.TopicAssignments.Add(template);
        await context.SaveChangesAsync();

        context.Assignments.Add(Assignment.CreateDraft(
            _organizationId, _headOfficeId, _sharpId, _mentorIds[0], _leadId, null,
            "Черновик", null, AssignedAt.AddDays(3), AssignedAt));

        context.Assignments.Add(Assignment.CreateSuggestion(
            _organizationId, _headOfficeId, _sharpId, _mentorIds[0], template.Id,
            "Предложение", null, AssignedAt.AddDays(3), DateOnly.FromDateTime(AssignedAt.UtcDateTime),
            $"{Guid.CreateVersion7():N}", AssignedAt));

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

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        // The body is in the message: a failed report is far easier to diagnose from its
        // ProblemDetails than from a NullReferenceException on the deserialised result.
        response.IsSuccessStatusCode.ShouldBeTrue($"{(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

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

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, Zone, Seeded);
        var khujand = Branch.Create(organization.Id, "Филиал Худжанд", "KHJ", null, Zone, Seeded);
        context.Branches.AddRange(headOffice, khujand);

        // The same name in two branches: two entities, and TEN-071 forbids merging them.
        var sharp = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        var khujandCategory = Category.Create(organization.Id, khujand.Id, "C#", null, Seeded);
        context.Categories.AddRange(sharp, khujandCategory);

        context.CategorySettings.AddRange(
            CategorySettings.CreateDefault(sharp, Zone, Seeded),
            CategorySettings.CreateDefault(khujandCategory, Zone, Seeded));

        var users = new List<User>
        {
            User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded),
            User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded),
            User.CreateLead(organization.Id, headOffice.Id, sharp.Id, "Лид C#", "lead-sharp@mentortaskflow.test", Seeded),
            User.CreateLead(organization.Id, khujand.Id, khujandCategory.Id, "Лид Худжанда", "lead-khujand@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, khujand.Id, khujandCategory.Id, "Ментор Худжанда", "mentor-khujand@mentortaskflow.test", Seeded),
        };

        // Five mentors in the head office: exactly the privacy threshold, so a test can sit on either
        // side of it by giving work to four or to five.
        for (var i = 1; i <= 5; i++)
        {
            users.Add(User.CreateMentor(
                organization.Id, headOffice.Id, sharp.Id, $"Ментор {i}", $"mentor{i}-head@mentortaskflow.test", Seeded));
        }

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
        _leadId = users.Single(u => u.Email == "lead-sharp@mentortaskflow.test").Id;
        _khujandMentorId = users.Single(u => u.Email == "mentor-khujand@mentortaskflow.test").Id;

        _mentorIds.AddRange(users
            .Where(u => u.Role == UserRole.Mentor && u.BranchId == headOffice.Id)
            .OrderBy(u => u.Email)
            .Select(u => u.Id));
    }
}
