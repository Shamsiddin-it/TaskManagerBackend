using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Reviews;
using MentorTaskFlow.Domain.Submissions;

namespace MentorTaskFlow.UnitTests.Reviews;

/// <summary>The invariants of a decision (TZ 10.8).</summary>
public sealed class ReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ReviewerId = Guid.CreateVersion7();

    [Fact]
    public void An_approval_carries_no_rework_deadline()
    {
        var review = Review.Approve(Submitted(), ReviewerId, comment: null, Now);

        review.Decision.ShouldBe(ReviewDecision.Approved);
        review.ReworkDueAt.ShouldBeNull();
        review.Comment.ShouldBeNull();
    }

    /// <summary>An approval may explain itself; it is not required to.</summary>
    [Fact]
    public void An_approval_may_carry_a_comment() =>
        Review.Approve(Submitted(), ReviewerId, "Замечаний нет.", Now).Comment.ShouldBe("Замечаний нет.");

    [Fact]
    public void An_approval_with_only_whitespace_for_a_comment_stores_none() =>
        Review.Approve(Submitted(), ReviewerId, "   ", Now).Comment.ShouldBeNull();

    [Fact]
    public void Rework_records_the_comment_and_the_new_deadline()
    {
        var due = Now.AddDays(3);

        var review = Review.RequestRework(Submitted(), ReviewerId, "Переделайте раздел про индексы.", due, Now);

        review.Decision.ShouldBe(ReviewDecision.NeedsRework);
        review.Comment.ShouldBe("Переделайте раздел про индексы.");
        review.ReworkDueAt.ShouldBe(due);
    }

    /// <summary>
    /// Rework without a reason the mentor can act on is not a decision — it is a rejection with no
    /// route forward, which is the one thing returning work is meant to provide.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("коротко")]
    public void Rework_needs_a_comment_of_at_least_ten_characters(string comment) =>
        Should.Throw<DomainException>(
                () => Review.RequestRework(Submitted(), ReviewerId, comment, Now.AddDays(3), Now))
            .Code.ShouldBe(DomainErrorCodes.ValidationFailed);

    [Fact]
    public void Rework_refuses_a_comment_over_three_thousand_characters() =>
        Should.Throw<DomainException>(() => Review.RequestRework(
            Submitted(),
            ReviewerId,
            new string('и', Review.CommentMaxLength + 1),
            Now.AddDays(3),
            Now));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rework_refuses_a_deadline_that_is_not_in_the_future(int daysFromNow) =>
        Should.Throw<DomainException>(() => Review.RequestRework(
            Submitted(),
            ReviewerId,
            "Переделайте раздел про индексы.",
            Now.AddDays(daysFromNow),
            Now));

    /// <summary>
    /// <c>REV-022</c>: unreachable in Release 1.0 — work goes only to mentors, and a Lead is never an
    /// executor — and kept deliberately. It guards a later model where a Lead can be assigned work, and
    /// a migration that gets the wiring wrong.
    /// </summary>
    [Fact]
    public void Reviewing_ones_own_work_is_refused()
    {
        var submission = Submitted();

        Should.Throw<DomainException>(
                () => Review.Approve(submission, submission.SubmittedById, comment: null, Now))
            .Code.ShouldBe(DomainErrorCodes.SelfReviewForbidden);
    }

    /// <summary>Scope is copied from the submission, never taken from the request (10.8).</summary>
    [Fact]
    public void Scope_comes_from_the_submission()
    {
        var submission = Submitted();

        var review = Review.Approve(submission, ReviewerId, comment: null, Now);

        review.SubmissionId.ShouldBe(submission.Id);
        review.AssignmentId.ShouldBe(submission.AssignmentId);
        review.OrganizationId.ShouldBe(submission.OrganizationId);
        review.BranchId.ShouldBe(submission.BranchId);
        review.CategoryId.ShouldBe(submission.CategoryId);
    }

    private static Submission Submitted()
    {
        var organizationId = Guid.CreateVersion7();
        var branchId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        var mentorId = Guid.CreateVersion7();

        var assignment = Assignment.CreateDraft(
            organizationId,
            branchId,
            categoryId,
            mentorId,
            Guid.CreateVersion7(),
            null,
            "Задача",
            null,
            Now.AddDays(3),
            Now);

        return Submission.Record(
            Guid.CreateVersion7(),
            assignment,
            versionNumber: 1,
            "submissions/key.pdf",
            "работа.pdf",
            FileExtension.Pdf,
            fileSizeBytes: 1024,
            new string('a', 64),
            isLate: false,
            mentorId,
            Now);
    }
}
