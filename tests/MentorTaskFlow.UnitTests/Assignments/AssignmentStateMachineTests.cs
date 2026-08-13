using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.UnitTests.Assignments;

/// <summary>
/// The state machine of TZ 13.3 and Приложение B, exhaustively.
/// </summary>
/// <remarks>
/// Section 13 is the single source of truth for statuses and transitions. These tests assert the
/// table as written: every permitted move, and — more importantly — that everything absent from it is
/// refused rather than silently allowed.
/// </remarks>
public sealed class AssignmentStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DueAt = Now.AddDays(3);

    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid BranchId = Guid.CreateVersion7();
    private static readonly Guid CategoryId = Guid.CreateVersion7();
    private static readonly Guid MentorId = Guid.CreateVersion7();
    private static readonly Guid LeadId = Guid.CreateVersion7();

    // -----------------------------------------------------------------
    // Creation
    // -----------------------------------------------------------------

    [Fact]
    public void A_draft_starts_unpublished_with_both_deadlines_equal()
    {
        var assignment = Draft();

        assignment.Status.ShouldBe(AssignmentStatus.Draft);
        assignment.Source.ShouldBe(AssignmentSource.Manual);
        assignment.InitialDueAt.ShouldBe(DueAt);
        assignment.CurrentDueAt.ShouldBe(DueAt);
        assignment.AssignedAt.ShouldBeNull();

        // SCH-012: a manual assignment carries neither auto-generation field, which is what keeps it
        // out of the idempotency index.
        assignment.GeneratedForDate.ShouldBeNull();
        assignment.AutoGenerationKey.ShouldBeNull();
    }

    /// <summary>
    /// <c>AssignedById</c> stays null until a Lead accepts: nobody has yet decided to hand this work
    /// out, and naming the scheduler would misattribute the decision.
    /// </summary>
    [Fact]
    public void A_suggestion_has_no_assigner_until_it_is_accepted()
    {
        var assignment = Suggestion();

        assignment.Status.ShouldBe(AssignmentStatus.Suggested);
        assignment.Source.ShouldBe(AssignmentSource.Auto);
        assignment.AssignedById.ShouldBeNull();
        assignment.AutoGenerationKey.ShouldNotBeNullOrWhiteSpace();

        assignment.AcceptSuggestion(LeadId, Now);
        assignment.AssignedById.ShouldBe(LeadId);
    }

    // -----------------------------------------------------------------
    // The permitted transitions of Приложение B
    // -----------------------------------------------------------------

    [Fact]
    public void Transition_1_draft_to_assigned()
    {
        var assignment = Draft();

        assignment.Publish(LeadId, Now);

        assignment.Status.ShouldBe(AssignmentStatus.Assigned);
        assignment.AssignedAt.ShouldBe(Now);
        assignment.AssignedById.ShouldBe(LeadId);
    }

    [Fact]
    public void Transition_3_suggested_to_assigned()
    {
        var assignment = Suggestion();

        assignment.AcceptSuggestion(LeadId, Now);

        assignment.Status.ShouldBe(AssignmentStatus.Assigned);
        assignment.AssignedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData(AssignmentStatus.Assigned)]
    [InlineData(AssignmentStatus.NeedsRework)]
    [InlineData(AssignmentStatus.Overdue)]
    public void Transitions_5_13_and_16_reach_submitted(AssignmentStatus from)
    {
        var assignment = InStatus(from);

        assignment.Submit(isFirstVersion: true, Now);

        assignment.Status.ShouldBe(AssignmentStatus.Submitted);
        assignment.FirstSubmittedAt.ShouldBe(Now);
    }

    /// <summary><c>FirstSubmittedAt</c> marks version 1 only; later versions leave it alone.</summary>
    [Fact]
    public void A_later_version_does_not_move_first_submitted_at()
    {
        var assignment = InStatus(AssignmentStatus.Assigned);
        assignment.Submit(isFirstVersion: true, Now);

        assignment.StartReview(Now);
        assignment.RequestRework(Now.AddDays(2), Now);
        assignment.Submit(isFirstVersion: false, Now.AddHours(1));

        assignment.FirstSubmittedAt.ShouldBe(Now);
    }

    [Fact]
    public void Transition_8_submitted_to_in_review()
    {
        var assignment = InStatus(AssignmentStatus.Submitted);

        assignment.StartReview(Now);

        assignment.Status.ShouldBe(AssignmentStatus.InReview);
        assignment.ReviewStartedAt.ShouldBe(Now);
    }

    /// <summary>A repeat call is refused: the review has already been started (Приложение B, row 8).</summary>
    [Fact]
    public void Starting_a_review_twice_is_refused()
    {
        var assignment = InStatus(AssignmentStatus.Submitted);
        assignment.StartReview(Now);

        Should.Throw<DomainException>(() => assignment.StartReview(Now))
            .Code.ShouldBe(DomainErrorCodes.AssignmentInvalidStatusTransition);
    }

    [Fact]
    public void Transition_10_in_review_to_approved()
    {
        var assignment = InStatus(AssignmentStatus.InReview);

        assignment.Approve(Now);

        assignment.Status.ShouldBe(AssignmentStatus.Approved);
        assignment.ApprovedAt.ShouldBe(Now);
    }

    /// <summary>The single point at which <c>CurrentDueAt</c> moves (14.1).</summary>
    [Fact]
    public void Transition_11_in_review_to_needs_rework_moves_the_working_deadline()
    {
        var assignment = InStatus(AssignmentStatus.InReview);
        var reworkDueAt = Now.AddDays(5);

        assignment.RequestRework(reworkDueAt, Now);

        assignment.Status.ShouldBe(AssignmentStatus.NeedsRework);
        assignment.CurrentDueAt.ShouldBe(reworkDueAt);

        // The original deadline is a historical fact and never moves.
        assignment.InitialDueAt.ShouldBe(DueAt);
    }

    /// <summary>
    /// A rework deadline already in the past would put the task straight back into overdue on the
    /// next scheduler run (<c>REV-002</c>).
    /// </summary>
    [Fact]
    public void A_rework_deadline_in_the_past_is_refused()
    {
        var assignment = InStatus(AssignmentStatus.InReview);

        Should.Throw<DomainException>(() => assignment.RequestRework(Now.AddMinutes(-1), Now))
            .Code.ShouldBe(DomainErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData(AssignmentStatus.Assigned)]
    [InlineData(AssignmentStatus.NeedsRework)]
    public void Transitions_6_and_14_reach_overdue(AssignmentStatus from)
    {
        var assignment = InStatus(from);

        assignment.MarkOverdue(Now);

        assignment.Status.ShouldBe(AssignmentStatus.Overdue);
        assignment.OverdueAt.ShouldBe(Now);
    }

    /// <summary>
    /// <c>OverdueAt</c> holds the first occurrence only. Overwriting it would make
    /// <c>OverdueRate</c> count events instead of tasks, which can exceed 100% (14.4).
    /// </summary>
    [Fact]
    public void A_second_overdue_does_not_overwrite_the_first_moment()
    {
        var assignment = InStatus(AssignmentStatus.Assigned);
        assignment.MarkOverdue(Now);

        assignment.Submit(isFirstVersion: true, Now);
        assignment.StartReview(Now);
        assignment.RequestRework(Now.AddDays(1), Now);

        var later = Now.AddDays(2);
        assignment.MarkOverdue(later);

        assignment.OverdueAt.ShouldBe(Now);
    }

    /// <summary>
    /// Submitted and InReview never go overdue: the work is already in and sitting with the Lead,
    /// whose slowness is a metric rather than the task's status (<c>SCH-022</c>).
    /// </summary>
    [Theory]
    [InlineData(AssignmentStatus.Draft)]
    [InlineData(AssignmentStatus.Suggested)]
    [InlineData(AssignmentStatus.Submitted)]
    [InlineData(AssignmentStatus.InReview)]
    public void Only_assigned_and_needs_rework_can_go_overdue(AssignmentStatus from)
    {
        var assignment = InStatus(from);

        Should.Throw<DomainException>(() => assignment.MarkOverdue(Now));
    }

    [Theory]
    [InlineData(AssignmentStatus.Draft)]
    [InlineData(AssignmentStatus.Suggested)]
    [InlineData(AssignmentStatus.Assigned)]
    [InlineData(AssignmentStatus.Submitted)]
    [InlineData(AssignmentStatus.InReview)]
    [InlineData(AssignmentStatus.NeedsRework)]
    [InlineData(AssignmentStatus.Overdue)]
    public void Cancellation_is_available_from_every_non_terminal_status(AssignmentStatus from)
    {
        var assignment = InStatus(from);

        assignment.Cancel(LeadId, "Отменено по решению тимлида", Now);

        assignment.Status.ShouldBe(AssignmentStatus.Cancelled);
        assignment.CancelledById.ShouldBe(LeadId);
        assignment.CancelledAt.ShouldBe(Now);
        assignment.CancelReason.ShouldBe("Отменено по решению тимлида");
    }

    [Theory]
    [InlineData("")]
    [InlineData("нет")]
    public void Cancellation_requires_a_reason(string reason)
    {
        var assignment = InStatus(AssignmentStatus.Assigned);

        Should.Throw<DomainException>(() => assignment.Cancel(LeadId, reason, Now))
            .Code.ShouldBe(DomainErrorCodes.ValidationFailed);
    }

    // -----------------------------------------------------------------
    // Terminal statuses
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>ASN-021</c>: the terminal code takes precedence over the generic transition error, so the
    /// interface can say «this task is finished» rather than «that move is not allowed here».
    /// </summary>
    [Theory]
    [InlineData(AssignmentStatus.Approved)]
    [InlineData(AssignmentStatus.Cancelled)]
    public void Every_action_on_a_terminal_assignment_reports_terminal(AssignmentStatus terminal)
    {
        Should.Throw<DomainException>(() => InStatus(terminal).Publish(LeadId, Now))
            .Code.ShouldBe(DomainErrorCodes.AssignmentTerminal);

        Should.Throw<DomainException>(() => InStatus(terminal).StartReview(Now))
            .Code.ShouldBe(DomainErrorCodes.AssignmentTerminal);

        Should.Throw<DomainException>(() => InStatus(terminal).Approve(Now))
            .Code.ShouldBe(DomainErrorCodes.AssignmentTerminal);

        Should.Throw<DomainException>(() => InStatus(terminal).Cancel(LeadId, "Повторная отмена", Now))
            .Code.ShouldBe(DomainErrorCodes.AssignmentTerminal);
    }

    // -----------------------------------------------------------------
    // Transitions absent from the table
    // -----------------------------------------------------------------

    public static TheoryData<AssignmentStatus> NonDraftStatuses() =>
    [
        AssignmentStatus.Suggested, AssignmentStatus.Assigned, AssignmentStatus.Submitted,
        AssignmentStatus.InReview, AssignmentStatus.NeedsRework, AssignmentStatus.Overdue,
    ];

    [Theory]
    [MemberData(nameof(NonDraftStatuses))]
    public void Publish_is_reachable_only_from_draft(AssignmentStatus from)
    {
        Should.Throw<DomainException>(() => InStatus(from).Publish(LeadId, Now))
            .Code.ShouldBe(DomainErrorCodes.AssignmentInvalidStatusTransition);
    }

    [Theory]
    [InlineData(AssignmentStatus.Draft)]
    [InlineData(AssignmentStatus.Submitted)]
    [InlineData(AssignmentStatus.InReview)]
    [InlineData(AssignmentStatus.Approved)]
    public void Submission_is_refused_outside_the_three_permitted_statuses(AssignmentStatus from)
    {
        Should.Throw<DomainException>(() => InStatus(from).Submit(isFirstVersion: true, Now));
    }

    [Theory]
    [InlineData(AssignmentStatus.Draft)]
    [InlineData(AssignmentStatus.Assigned)]
    [InlineData(AssignmentStatus.Overdue)]
    public void Approval_is_reachable_only_from_in_review(AssignmentStatus from)
    {
        Should.Throw<DomainException>(() => InStatus(from).Approve(Now));
    }

    // -----------------------------------------------------------------
    // Editing and reassignment
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(AssignmentStatus.Draft)]
    [InlineData(AssignmentStatus.Suggested)]
    public void An_unpublished_assignment_can_be_edited(AssignmentStatus from)
    {
        var assignment = InStatus(from);
        var newDueAt = Now.AddDays(7);

        assignment.Edit("Новое название", "Новое описание", newDueAt, Now);

        assignment.Title.ShouldBe("Новое название");
        assignment.InitialDueAt.ShouldBe(newDueAt);
        assignment.CurrentDueAt.ShouldBe(newDueAt);
    }

    /// <summary>
    /// After publication the title, description and deadline are the terms the mentor was given;
    /// editing them would rewrite work already under way (<c>ASN-004</c>).
    /// </summary>
    [Theory]
    [InlineData(AssignmentStatus.Assigned)]
    [InlineData(AssignmentStatus.Submitted)]
    [InlineData(AssignmentStatus.NeedsRework)]
    public void A_published_assignment_cannot_be_edited(AssignmentStatus from)
    {
        Should.Throw<DomainException>(() => InStatus(from).Edit("Новое", null, Now.AddDays(7), Now));
    }

    [Theory]
    [InlineData(AssignmentStatus.Draft)]
    [InlineData(AssignmentStatus.Suggested)]
    [InlineData(AssignmentStatus.Assigned)]
    public void Reassignment_is_allowed_while_no_submission_exists(AssignmentStatus from)
    {
        var assignment = InStatus(from);
        var newMentor = Guid.CreateVersion7();

        assignment.Reassign(newMentor, hasSubmissions: false, Now);

        assignment.AssignedToId.ShouldBe(newMentor);

        // The status is untouched: reassignment changes the executor, not the state of the work.
        assignment.Status.ShouldBe(from);
    }

    /// <summary>
    /// After the first submission the executor is fixed for good: reassigning would leave one
    /// person's work attached to a task now owned by another (10.6.3).
    /// </summary>
    [Fact]
    public void Reassignment_is_refused_once_a_submission_exists()
    {
        var assignment = InStatus(AssignmentStatus.Assigned);

        Should.Throw<DomainException>(() => assignment.Reassign(Guid.CreateVersion7(), hasSubmissions: true, Now))
            .Code.ShouldBe(DomainErrorCodes.ReassignNotAllowed);
    }

    [Theory]
    [InlineData(AssignmentStatus.Submitted)]
    [InlineData(AssignmentStatus.InReview)]
    [InlineData(AssignmentStatus.NeedsRework)]
    [InlineData(AssignmentStatus.Overdue)]
    public void Reassignment_is_refused_outside_the_three_permitted_statuses(AssignmentStatus from)
    {
        Should.Throw<DomainException>(() => InStatus(from).Reassign(Guid.CreateVersion7(), hasSubmissions: false, Now))
            .Code.ShouldBe(DomainErrorCodes.ReassignNotAllowed);
    }

    // -----------------------------------------------------------------
    // Sequence numbers and in-flight
    // -----------------------------------------------------------------

    [Fact]
    public void Event_sequence_numbers_rise_from_one()
    {
        var assignment = Draft();

        assignment.NextEventSequence().ShouldBe(1);
        assignment.NextEventSequence().ShouldBe(2);
        assignment.LastEventSequence.ShouldBe(2);
    }

    /// <summary>
    /// The condition <c>USER-012</c> and <c>BRN-038</c> test before permitting a transfer: work in
    /// flight would otherwise stay with somebody no longer in the contour.
    /// </summary>
    [Theory]
    [InlineData(AssignmentStatus.Draft, true)]
    [InlineData(AssignmentStatus.Assigned, true)]
    [InlineData(AssignmentStatus.Overdue, true)]
    [InlineData(AssignmentStatus.Approved, false)]
    [InlineData(AssignmentStatus.Cancelled, false)]
    public void In_flight_covers_every_non_terminal_status(AssignmentStatus status, bool expected)
    {
        InStatus(status).IsInFlight.ShouldBe(expected);
    }

    // -----------------------------------------------------------------
    // Builders
    // -----------------------------------------------------------------

    private static Assignment Draft() => Assignment.CreateDraft(
        Org, BranchId, CategoryId, MentorId, LeadId, null, "Задача", "Описание", DueAt, Now);

    private static Assignment Suggestion() => Assignment.CreateSuggestion(
        Org, BranchId, CategoryId, MentorId, Guid.CreateVersion7(), "Задача", null, DueAt,
        new DateOnly(2026, 8, 13), "key", Now);

    /// <summary>Drives a fresh assignment to <paramref name="status"/> along the permitted path.</summary>
    private static Assignment InStatus(AssignmentStatus status)
    {
        if (status is AssignmentStatus.Suggested)
        {
            return Suggestion();
        }

        var assignment = Draft();

        if (status is AssignmentStatus.Draft)
        {
            return assignment;
        }

        assignment.Publish(LeadId, Now);

        switch (status)
        {
            case AssignmentStatus.Assigned:
                return assignment;

            case AssignmentStatus.Overdue:
                assignment.MarkOverdue(Now);
                return assignment;
        }

        assignment.Submit(isFirstVersion: true, Now);

        if (status is AssignmentStatus.Submitted)
        {
            return assignment;
        }

        assignment.StartReview(Now);

        switch (status)
        {
            case AssignmentStatus.InReview:
                return assignment;

            case AssignmentStatus.NeedsRework:
                assignment.RequestRework(Now.AddDays(2), Now);
                return assignment;

            case AssignmentStatus.Approved:
                assignment.Approve(Now);
                return assignment;

            case AssignmentStatus.Cancelled:
                assignment.Cancel(LeadId, "Отменено по решению тимлида", Now);
                return assignment;
        }

        throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled status.");
    }
}
