using System.Diagnostics.Metrics;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Schedule;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Common;
using MentorTaskFlow.Infrastructure.Notifications;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using MentorTaskFlow.Infrastructure.Scheduling;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.IntegrationTests.Scheduling;

/// <summary>Auto-generation, BDA, the overdue pass and reminders (TZ 20).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SchedulerJobTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Zone = "Asia/Dushanbe";

    /// <summary>06:00 in Dushanbe (UTC+5) on the planned date, which is when the job would fire.</summary>
    private static readonly DateTimeOffset RunAt = new(2026, 9, 1, 1, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly PlannedDate = new(2026, 9, 1);
    private static readonly DateTimeOffset Seeded = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private Guid _organizationId;
    private Guid _headOfficeId;
    private Guid _khujandId;
    private Guid _headCategoryId;
    private Guid _khujandCategoryId;
    private Guid _headMentorId;
    private Guid _secondHeadMentorId;
    private Guid _khujandMentorId;
    private Guid _headLeadId;
    private Guid _templateId;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // -----------------------------------------------------------------
    // Auto-generation (SCH-002, SCH-004)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_suggestion_is_generated_for_the_planned_date()
    {
        await RunGenerationAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var suggestion = await context.Assignments.SingleAsync(a => a.BranchId == _headOfficeId);

        suggestion.Status.ShouldBe(AssignmentStatus.Suggested);
        suggestion.Source.ShouldBe(AssignmentSource.Auto);
        suggestion.AssignedById.ShouldBeNull();
        suggestion.GeneratedForDate.ShouldBe(PlannedDate);
        suggestion.TopicAssignmentId.ShouldBe(_templateId);

        // SCH-020: planned date plus the category's due days, at its default time, in its zone.
        // 4 September 23:59 in Dushanbe is 18:59 UTC.
        suggestion.InitialDueAt.ShouldBe(new DateTimeOffset(2026, 9, 4, 18, 59, 0, TimeSpan.Zero));
        suggestion.CurrentDueAt.ShouldBe(suggestion.InitialDueAt);
    }

    [Fact]
    public async Task Generation_records_a_system_event_and_notifies_the_lead()
    {
        await RunGenerationAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var recorded = await context.TaskEvents
            .SingleAsync(e => e.EventType == TaskEventType.SuggestedCreated && e.BranchId == _headOfficeId);

        // 10.9: a system event carries no actor.
        recorded.ActorId.ShouldBeNull();
        recorded.NewStatus.ShouldBe(AssignmentStatus.Suggested);

        var notification = await context.NotificationOutbox
            .SingleAsync(n => n.EventType == NotificationEventTypes.AssignmentSuggested
                              && n.BranchId == _headOfficeId);

        notification.UserId.ShouldBe(_headLeadId);
    }

    /// <summary>
    /// <c>SCH-010</c> and <c>SCH-011</c>: a second run of the same day changes nothing, whatever the
    /// Lead has since done with the suggestion.
    /// </summary>
    [Fact]
    public async Task A_second_run_creates_nothing()
    {
        await RunGenerationAsync();
        await RunGenerationAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.Assignments.CountAsync(a => a.BranchId == _headOfficeId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_rejected_suggestion_is_not_regenerated()
    {
        await RunGenerationAsync();

        await using (var context = postgres.CreateContext(suppressTenantFilter: true))
        {
            var suggestion = await context.Assignments.SingleAsync(a => a.BranchId == _headOfficeId);
            suggestion.Cancel(_headLeadId, "Не требуется в этом потоке", RunAt);

            await context.SaveChangesAsync();
        }

        await RunGenerationAsync();

        await using var verify = postgres.CreateContext(suppressTenantFilter: true);
        var rows = await verify.Assignments.Where(a => a.BranchId == _headOfficeId).ToListAsync();

        rows.ShouldHaveSingleItem().Status.ShouldBe(AssignmentStatus.Cancelled);
    }

    /// <summary><c>TEN-050</c>: the chain breaks at the first inactive link.</summary>
    [Theory]
    [InlineData("organization")]
    [InlineData("branch")]
    [InlineData("category")]
    [InlineData("topic")]
    [InlineData("template")]
    public async Task An_inactive_link_stops_generation(string link)
    {
        await DeactivateAsync(link);
        await RunGenerationAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var affected = link is "branch" ? _khujandId : _headOfficeId;

        (await context.Assignments.CountAsync(a => a.BranchId == affected)).ShouldBe(0);
    }

    /// <summary>
    /// <c>TEST-TEN-022</c>: a deactivated branch produces nothing, says so in the metric, and does not
    /// disturb its neighbours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counter is the part worth insisting on. A deactivated branch and a branch whose scheduler
    /// silently failed both produce zero suggestions, and only one of them is an incident — without
    /// <c>scheduler_skipped_total{reason="branch_inactive"}</c> the alert of <c>OBS-012</c> cannot tell
    /// them apart and would either cry wolf every night or stay silent through a real outage.
    /// </para>
    /// <para>
    /// The third assertion is the one people forget: deactivating a branch must be a local event. A
    /// chain that broke for the whole run rather than for one branch would show up here as an empty
    /// head office.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_deactivated_branch_is_skipped_counted_and_leaves_its_neighbours_alone()
    {
        // Khujand is seeded with the same topic and template as the head office, so it would
        // generate today were the branch active.
        await DeactivateAsync("branch");

        var skipped = new List<string>();
        await RunGenerationAsync(reason => skipped.Add(reason));

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        // Nothing in the deactivated branch.
        (await context.Assignments.CountAsync(a => a.BranchId == _khujandId)).ShouldBe(0);

        // Counted, and by the link that broke.
        skipped.ShouldContain("branch_inactive");

        // The neighbour ran normally — deactivation is one branch's event, not the run's.
        (await context.Assignments.CountAsync(a => a.BranchId == _headOfficeId)).ShouldBe(1);
    }

    /// <summary>Optional templates are not generated: only <c>IsRequired</c> ones (<c>SCH-003</c>).</summary>
    [Fact]
    public async Task An_optional_template_is_not_generated()
    {
        await using (var context = postgres.CreateContext(suppressTenantFilter: true))
        {
            var template = await context.TopicAssignments.SingleAsync(t => t.Id == _templateId);
            template.Update(TopicAssignmentType.HomeTask, "Домашнее задание", null, false, Seeded);

            await context.SaveChangesAsync();
        }

        await RunGenerationAsync();

        await using var verify = postgres.CreateContext(suppressTenantFilter: true);
        (await verify.Assignments.CountAsync(a => a.BranchId == _headOfficeId)).ShouldBe(0);
    }

    /// <summary>
    /// <c>SCH-016</c>: no active mentor stops the suggestion but not the job, and the people who can
    /// fix it are told.
    /// </summary>
    [Fact]
    public async Task A_category_without_mentors_is_reported_rather_than_failing()
    {
        await using (var context = postgres.CreateContext(suppressTenantFilter: true))
        {
            foreach (var mentor in await context.Users
                         .Where(u => u.CategoryId == _headCategoryId && u.Role == UserRole.Mentor)
                         .ToListAsync())
            {
                mentor.Deactivate(Seeded);
            }

            await context.SaveChangesAsync();
        }

        await RunGenerationAsync();

        await using var verify = postgres.CreateContext(suppressTenantFilter: true);

        (await verify.Assignments.CountAsync(a => a.BranchId == _headOfficeId)).ShouldBe(0);

        var audit = await verify.AuditLogs.SingleAsync(a => a.Action == AuditActions.SchedulerNoActiveMentor);
        audit.Result.ShouldBe(AuditResult.Failure);
        audit.BranchId.ShouldBe(_headOfficeId);

        (await verify.NotificationOutbox
                .AnyAsync(n => n.EventType == NotificationEventTypes.SchedulerNoActiveMentor))
            .ShouldBeTrue();
    }

    // -----------------------------------------------------------------
    // BDA (TEN-051, SCH-013, SCH-014)
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-020</c> and <c>TEST-TEN-021</c>: a mentor of another branch is never selected,
    /// however identically their category is named.
    /// </summary>
    [Fact]
    public async Task A_mentor_of_another_branch_is_never_selected()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var selector = new MentorSelector(context);

        var selected = await selector.SelectAsync(
            _organizationId,
            _headOfficeId,
            _headCategoryId,
            CancellationToken.None);

        selected.ShouldNotBe(_khujandMentorId);
        new[] { _headMentorId, _secondHeadMentorId }.ShouldContain(selected!.Value);
    }

    [Fact]
    public async Task Generation_never_crosses_into_another_branch()
    {
        await RunGenerationAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var headSuggestion = await context.Assignments.SingleAsync(a => a.BranchId == _headOfficeId);
        new[] { _headMentorId, _secondHeadMentorId }.ShouldContain(headSuggestion.AssignedToId);

        var khujandSuggestion = await context.Assignments.SingleAsync(a => a.BranchId == _khujandId);
        khujandSuggestion.AssignedToId.ShouldBe(_khujandMentorId);
    }

    /// <summary>The least loaded mentor wins (<c>SCH-013</c>, step 1).</summary>
    [Fact]
    public async Task The_least_loaded_mentor_is_selected()
    {
        await GiveOpenWorkAsync(_headMentorId, count: 2);
        await GiveOpenWorkAsync(_secondHeadMentorId, count: 1);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await new MentorSelector(context).SelectAsync(
                _organizationId, _headOfficeId, _headCategoryId, CancellationToken.None))
            .ShouldBe(_secondHeadMentorId);
    }

    /// <summary>
    /// On equal load the earliest last assignment wins, and never having been assigned counts as
    /// earliest — so a newcomer is served first (<c>SCH-013</c>, step 2).
    /// </summary>
    [Fact]
    public async Task A_mentor_who_has_never_been_assigned_goes_first()
    {
        await GiveClosedWorkAsync(_headMentorId, RunAt.AddDays(-1));

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await new MentorSelector(context).SelectAsync(
                _organizationId, _headOfficeId, _headCategoryId, CancellationToken.None))
            .ShouldBe(_secondHeadMentorId);
    }

    /// <summary><c>TEST-SCH-004</c>: the same state selects the same mentor, every time.</summary>
    [Fact]
    public async Task The_selection_is_deterministic()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var selector = new MentorSelector(context);

        var first = await selector.SelectAsync(_organizationId, _headOfficeId, _headCategoryId, CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            (await selector.SelectAsync(_organizationId, _headOfficeId, _headCategoryId, CancellationToken.None))
                .ShouldBe(first);
        }

        // The total order ends in the smallest identifier, so with everything else equal the outcome
        // is not merely stable but predictable.
        first.ShouldBe(new[] { _headMentorId, _secondHeadMentorId }.Min());
    }

    [Fact]
    public async Task A_deactivated_mentor_is_not_selected()
    {
        await using (var context = postgres.CreateContext(suppressTenantFilter: true))
        {
            var mentor = await context.Users.SingleAsync(u => u.Id == new[] { _headMentorId, _secondHeadMentorId }.Min());
            mentor.Deactivate(Seeded);

            await context.SaveChangesAsync();
        }

        await using var verify = postgres.CreateContext(suppressTenantFilter: true);

        (await new MentorSelector(verify).SelectAsync(
                _organizationId, _headOfficeId, _headCategoryId, CancellationToken.None))
            .ShouldBe(new[] { _headMentorId, _secondHeadMentorId }.Max());
    }

    // -----------------------------------------------------------------
    // Overdue (SCH-007, SCH-019)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Work_past_its_deadline_is_marked_overdue()
    {
        var assignmentId = await PublishOverdueAsync(_headMentorId);

        await RunOverdueAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var assignment = await context.Assignments.SingleAsync(a => a.Id == assignmentId);

        assignment.Status.ShouldBe(AssignmentStatus.Overdue);
        assignment.OverdueAt.ShouldNotBeNull();

        var recorded = await context.TaskEvents.SingleAsync(e => e.EventType == TaskEventType.MarkedOverdue);
        recorded.ActorId.ShouldBeNull();
        recorded.PreviousStatus.ShouldBe(AssignmentStatus.Assigned);

        (await context.NotificationOutbox
                .CountAsync(n => n.EventType == NotificationEventTypes.AssignmentOverdue))
            .ShouldBe(2);
    }

    /// <summary>
    /// The conditional update makes the pass idempotent: a second run touches nothing and produces no
    /// second event (<c>SCH-007</c>).
    /// </summary>
    [Fact]
    public async Task A_second_overdue_pass_changes_nothing()
    {
        await PublishOverdueAsync(_headMentorId);

        await RunOverdueAsync();
        await RunOverdueAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.TaskEvents.CountAsync(e => e.EventType == TaskEventType.MarkedOverdue)).ShouldBe(1);
    }

    /// <summary>Submitted work is with the Lead; a slow review is not the mentor's overdue (14.4).</summary>
    [Fact]
    public async Task Submitted_work_is_never_marked_overdue()
    {
        var assignmentId = await PublishOverdueAsync(_headMentorId);

        await using (var context = postgres.CreateContext(suppressTenantFilter: true))
        {
            var assignment = await context.Assignments.SingleAsync(a => a.Id == assignmentId);
            assignment.Submit(isFirstVersion: true, RunAt);

            await context.SaveChangesAsync();
        }

        await RunOverdueAsync();

        await using var verify = postgres.CreateContext(suppressTenantFilter: true);
        (await verify.Assignments.SingleAsync(a => a.Id == assignmentId)).Status
            .ShouldBe(AssignmentStatus.Submitted);
    }

    /// <summary><c>BRN-033</c>: nobody in a closed branch can act, so nothing there goes overdue.</summary>
    [Fact]
    public async Task Work_in_a_deactivated_branch_is_left_alone()
    {
        // Khujand rather than the head office: BRN-034 forbids deactivating the branch that holds the
        // head-office flag, and the rule under test is about a closed branch, not about which one.
        var assignmentId = await PublishOverdueInKhujandAsync();

        await using (var context = postgres.CreateContext(suppressTenantFilter: true))
        {
            var branch = await context.Branches.SingleAsync(b => b.Id == _khujandId);
            branch.Deactivate(Seeded);

            await context.SaveChangesAsync();
        }

        await RunOverdueAsync();

        await using var verify = postgres.CreateContext(suppressTenantFilter: true);
        (await verify.Assignments.SingleAsync(a => a.Id == assignmentId)).Status
            .ShouldBe(AssignmentStatus.Assigned);
    }

    // -----------------------------------------------------------------
    // Reminders (NTF-003, NTF-004)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_deadline_inside_the_window_produces_one_reminder()
    {
        await PublishWithDeadlineAsync(_headMentorId, RunAt.AddHours(12));

        await RunRemindersAsync();
        await RunRemindersAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        // NTF-004: the key carries the deadline value, so repeated passes send exactly once.
        (await context.NotificationOutbox
                .CountAsync(n => n.EventType == NotificationEventTypes.DeadlineReminder))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_deadline_outside_the_window_produces_none()
    {
        await PublishWithDeadlineAsync(_headMentorId, RunAt.AddDays(10));

        await RunRemindersAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.NotificationOutbox
                .AnyAsync(n => n.EventType == NotificationEventTypes.DeadlineReminder))
            .ShouldBeFalse();
    }

    /// <summary>Past the deadline there is nothing to remind about — that is the overdue pass's job.</summary>
    [Fact]
    public async Task A_deadline_already_passed_produces_none()
    {
        await PublishOverdueAsync(_headMentorId);

        await RunRemindersAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.NotificationOutbox
                .AnyAsync(n => n.EventType == NotificationEventTypes.DeadlineReminder))
            .ShouldBeFalse();
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    private async Task RunGenerationAsync(Action<string>? onSkipped = null)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var clock = new FixedClock(RunAt);

        using var listener = onSkipped is null ? null : ListenForSkips(onSkipped);

        var job = new AutoGenerationJob(
            context,
            new MentorSelector(context),
            new DeadlineCalculator(NullLogger<DeadlineCalculator>.Instance),
            Writer(context, clock),
            new NoopAuditWriter(context, clock),
            listener?.Metrics ?? Metrics(),
            NullLogger<AutoGenerationJob>.Instance,
            clock);

        await job.RunAsync(Zone, CancellationToken.None);
    }

    /// <summary>
    /// Observes <c>scheduler_skipped_total</c> as the job records it.
    /// </summary>
    /// <remarks>
    /// A <see cref="MeterListener"/> rather than a scrape of <c>/metrics</c>: the job runs here in
    /// process, without a host, and the assertion is about the reason label — which is what the alert
    /// of <c>OBS-012</c> keys on.
    /// </remarks>
    private static SkipListener ListenForSkips(Action<string> onSkipped)
    {
        var factory = MeterFactory();
        var metrics = new SchedulerMetrics(factory);

        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SchedulerMetrics.MeterName
                    && instrument.Name == "scheduler_skipped_total")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && tag.Value is string reason)
                {
                    onSkipped(reason);
                }
            }
        });

        listener.Start();

        return new SkipListener(metrics, listener);
    }

    private sealed record SkipListener(SchedulerMetrics Metrics, MeterListener Listener) : IDisposable
    {
        public void Dispose() => Listener.Dispose();
    }

    private async Task RunOverdueAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var clock = new FixedClock(RunAt);

        var job = new OverdueJob(
            context,
            Writer(context, clock),
            Metrics(),
            Options.Create(new SchedulerOptions { OverdueBatchSize = 200 }),
            NullLogger<OverdueJob>.Instance,
            clock);

        await job.RunAsync(CancellationToken.None);
    }

    private async Task RunRemindersAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var clock = new FixedClock(RunAt);

        var job = new DeadlineReminderJob(
            context,
            Writer(context, clock),
            Metrics(),
            NullLogger<DeadlineReminderJob>.Instance,
            clock);

        await job.RunAsync(CancellationToken.None);
    }

    private OutboxWriter Writer(MentorTaskFlowDbContext context, IClock clock) =>
        new(context, new StubBranchContext(_organizationId, _headOfficeId), NotificationMetrics(), clock);

    private static SchedulerMetrics Metrics() => new(MeterFactory());

    private static NotificationMetrics NotificationMetrics() => new(MeterFactory());

    private static IMeterFactory MeterFactory() =>
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    private async Task DeactivateAsync(string link)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        switch (link)
        {
            case "organization":
                (await context.Organizations.SingleAsync()).Deactivate(Seeded);
                break;

            // BRN-034 forbids deactivating the head office while it holds the flag, so the branch
            // link is exercised on Khujand — the rule under test is about the chain, not about which
            // branch it is.
            case "branch":
                (await context.Branches.SingleAsync(b => b.Id == _khujandId)).Deactivate(Seeded);
                break;

            case "category":
                (await context.Categories.SingleAsync(c => c.Id == _headCategoryId)).Deactivate(Seeded);
                break;

            case "topic":
                (await context.Topics.SingleAsync(t => t.CategoryId == _headCategoryId)).Deactivate(Seeded);
                break;

            case "template":
                (await context.TopicAssignments.SingleAsync(t => t.Id == _templateId)).Deactivate(Seeded);
                break;
        }

        await context.SaveChangesAsync();
    }

    private async Task GiveOpenWorkAsync(Guid mentorId, int count)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        for (var i = 0; i < count; i++)
        {
            var assignment = Assignment.CreateDraft(
                _organizationId, _headOfficeId, _headCategoryId, mentorId, _headLeadId, null,
                $"Открытая задача {i}", null, RunAt.AddDays(3), RunAt.AddDays(-2));

            assignment.Publish(_headLeadId, RunAt.AddDays(-2));
            context.Assignments.Add(assignment);
        }

        await context.SaveChangesAsync();
    }

    private async Task GiveClosedWorkAsync(Guid mentorId, DateTimeOffset assignedAt)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var assignment = Assignment.CreateDraft(
            _organizationId, _headOfficeId, _headCategoryId, mentorId, _headLeadId, null,
            "Завершённая задача", null, assignedAt.AddDays(3), assignedAt);

        assignment.Publish(_headLeadId, assignedAt);
        assignment.Submit(isFirstVersion: true, assignedAt.AddHours(1));
        assignment.StartReview(assignedAt.AddHours(2));
        assignment.Approve(assignedAt.AddHours(3));

        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();
    }

    private Task<Guid> PublishOverdueAsync(Guid mentorId) =>
        PublishWithDeadlineAsync(mentorId, RunAt.AddHours(-1));

    private async Task<Guid> PublishOverdueInKhujandAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var lead = User.CreateLead(
            _organizationId, _khujandId, _khujandCategoryId, "Лид Худжанда", "lead-khujand@mentortaskflow.test", Seeded);

        context.Users.Add(lead);
        await context.SaveChangesAsync();

        var assignment = Assignment.CreateDraft(
            _organizationId, _khujandId, _khujandCategoryId, _khujandMentorId, lead.Id, null,
            "Просроченная задача", null, RunAt.AddHours(-1), RunAt.AddDays(-3));

        assignment.Publish(lead.Id, RunAt.AddDays(-3));

        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();

        return assignment.Id;
    }

    private async Task<Guid> PublishWithDeadlineAsync(Guid mentorId, DateTimeOffset dueAt)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var assignment = Assignment.CreateDraft(
            _organizationId, _headOfficeId, _headCategoryId, mentorId, _headLeadId, null,
            "Задача с дедлайном", null, dueAt, RunAt.AddDays(-3));

        assignment.Publish(_headLeadId, RunAt.AddDays(-3));

        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();

        return assignment.Id;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StubBranchContext(Guid organizationId, Guid branchId)
        : Application.Common.Tenancy.IBranchContext
    {
        public Guid EffectiveOrganizationId => organizationId;

        public Guid? EffectiveBranchId => branchId;

        public bool IsAllBranchesReadContext => false;

        public bool CanOverrideBranch => false;

        public Guid RequireBranchForMutation() => branchId;
    }

    /// <summary>Writes system audit rows without a request context, as a job does.</summary>
    private sealed class NoopAuditWriter(MentorTaskFlowDbContext context, IClock clock) : IAuditWriter
    {
        public void Write(AuditEntry entry) => throw new NotSupportedException("Jobs write system entries.");

        public void WriteSystem(AuditEntry entry, Guid organizationId, Guid? branchId, Guid? correlationId = null) =>
            context.AuditLogs.Add(AuditLog.Record(
                entry.Action,
                entry.EntityType,
                organizationId,
                branchId,
                entry.CategoryId,
                entry.EntityId,
                AuditActorType.System,
                actorId: null,
                actorRole: null,
                actorAdminScope: null,
                entry.Result,
                correlationId ?? Guid.CreateVersion7(),
                clock.UtcNow,
                failureReason: entry.FailureReason,
                metadata: entry.Metadata));
    }

    private async Task SeedAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, Zone, Seeded);
        var khujand = Branch.Create(organization.Id, "Филиал Худжанд", "KHJ", null, Zone, Seeded);
        context.Branches.AddRange(headOffice, khujand);

        var headCategory = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        var khujandCategory = Category.Create(organization.Id, khujand.Id, "C#", null, Seeded);
        context.Categories.AddRange(headCategory, khujandCategory);

        context.CategorySettings.AddRange(
            CategorySettings.CreateDefault(headCategory, Zone, Seeded),
            CategorySettings.CreateDefault(khujandCategory, Zone, Seeded));

        var headTopic = Topic.Create(organization.Id, headOffice.Id, headCategory.Id, 1, PlannedDate, "Введение", null, Seeded);
        var khujandTopic = Topic.Create(organization.Id, khujand.Id, khujandCategory.Id, 1, PlannedDate, "Введение", null, Seeded);
        context.Topics.AddRange(headTopic, khujandTopic);

        var headTemplate = TopicAssignment.Create(headTopic, TopicAssignmentType.HomeTask, "Домашнее задание", null, true, Seeded);
        var khujandTemplate = TopicAssignment.Create(khujandTopic, TopicAssignmentType.HomeTask, "Домашнее задание", null, true, Seeded);
        context.TopicAssignments.AddRange(headTemplate, khujandTemplate);

        var headLead = User.CreateLead(organization.Id, headOffice.Id, headCategory.Id, "Лид", "lead-head@mentortaskflow.test", Seeded);
        var headMentor = User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded);
        var secondMentor = User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Второй ментор", "mentor2-head@mentortaskflow.test", Seeded);
        var khujandMentor = User.CreateMentor(organization.Id, khujand.Id, khujandCategory.Id, "Ментор Худжанда", "mentor-khujand@mentortaskflow.test", Seeded);

        context.Users.AddRange(headLead, headMentor, secondMentor, khujandMentor);
        await context.SaveChangesAsync();

        _organizationId = organization.Id;
        _headOfficeId = headOffice.Id;
        _khujandId = khujand.Id;
        _headCategoryId = headCategory.Id;
        _khujandCategoryId = khujandCategory.Id;
        _headMentorId = headMentor.Id;
        _secondHeadMentorId = secondMentor.Id;
        _khujandMentorId = khujandMentor.Id;
        _headLeadId = headLead.Id;
        _templateId = headTemplate.Id;
    }
}
