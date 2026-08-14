using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Submissions;

namespace MentorTaskFlow.Domain.Reviews;

/// <summary>The Lead's verdict on one version of the work (TZ 10.8).</summary>
public enum ReviewDecision
{
    Approved = 0,
    NeedsRework = 1,
}

/// <summary>
/// One decision on one submission (TZ 10.8, 15.7).
/// </summary>
/// <remarks>
/// <para>
/// Immutable, and for an Admin too (<c>REV-020</c>). A verdict that could be edited after the fact
/// would make the history of a task unusable in exactly the dispute it exists to settle — so there is
/// no update path, no concurrency token, and no <c>UpdatedAt</c>.
/// </para>
/// <para>
/// At most one review per submission, guaranteed by <c>ux_reviews_submission</c> rather than by a
/// prior lookup: two concurrent decisions would both find none and both write (<c>REV-005</c>).
/// </para>
/// </remarks>
public sealed class Review : BaseEntity
{
    public const int CommentMinLength = 10;
    public const int CommentMaxLength = 3000;

    public Guid SubmissionId { get; private set; }

    /// <summary>Denormalised from the submission for the query filter and the indexes (10.8).</summary>
    public Guid AssignmentId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid CategoryId { get; private set; }

    /// <summary>Taken from the token on the server; a value in the request body is a 400 (<c>REV-006</c>).</summary>
    public Guid ReviewerId { get; private set; }

    public ReviewDecision Decision { get; private set; }

    /// <summary>Required for <see cref="ReviewDecision.NeedsRework"/>, optional for an approval.</summary>
    public string? Comment { get; private set; }

    /// <summary>
    /// The new deadline, required for rework and forbidden for an approval. Becomes the assignment's
    /// <c>CurrentDueAt</c> — the only transition that moves it (14.1).
    /// </summary>
    public DateTimeOffset? ReworkDueAt { get; private set; }

    private Review()
    {
    }

    public static Review Approve(Submission submission, Guid reviewerId, string? comment, DateTimeOffset now) =>
        Record(submission, reviewerId, ReviewDecision.Approved, comment, reworkDueAt: null, now);

    public static Review RequestRework(
        Submission submission,
        Guid reviewerId,
        string comment,
        DateTimeOffset reworkDueAt,
        DateTimeOffset now) =>
        Record(submission, reviewerId, ReviewDecision.NeedsRework, comment, reworkDueAt, now);

    private static Review Record(
        Submission submission,
        Guid reviewerId,
        ReviewDecision decision,
        string? comment,
        DateTimeOffset? reworkDueAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var trimmedComment = comment?.Trim();

        // Mirrors ck_reviews_decision_fields. Rework without a reason and a new date is not a decision
        // the mentor can act on, which is the whole purpose of returning the work.
        if (decision is ReviewDecision.NeedsRework)
        {
            if (trimmedComment is not { Length: >= CommentMinLength and <= CommentMaxLength })
            {
                throw new DomainException(
                    DomainErrorCodes.ValidationFailed,
                    $"При возврате на доработку комментарий обязателен: от {CommentMinLength} до {CommentMaxLength} символов.");
            }

            if (reworkDueAt is not { } due)
            {
                throw new DomainException(
                    DomainErrorCodes.ValidationFailed,
                    "При возврате на доработку обязателен новый дедлайн.");
            }

            if (due <= now)
            {
                throw new DomainException(
                    DomainErrorCodes.ValidationFailed,
                    "Новый дедлайн должен быть в будущем.");
            }
        }
        else
        {
            if (reworkDueAt is not null)
            {
                throw new DomainException(
                    DomainErrorCodes.ValidationFailed,
                    "При одобрении новый дедлайн не указывается.");
            }

            if (trimmedComment is { Length: > CommentMaxLength })
            {
                throw new DomainException(
                    DomainErrorCodes.ValidationFailed,
                    $"Комментарий не длиннее {CommentMaxLength} символов.");
            }
        }

        // REV-022: a defensive invariant. By the data model a Lead is never an assignment's executor —
        // work goes to mentors — so this is unreachable in Release 1.0. It is kept deliberately: it
        // guards against a later model where a Lead can be assigned work, and against a bad migration.
        if (reviewerId == submission.SubmittedById)
        {
            throw new DomainException(
                DomainErrorCodes.SelfReviewForbidden,
                "Проверять собственную работу нельзя.");
        }

        return new Review
        {
            SubmissionId = submission.Id,
            AssignmentId = submission.AssignmentId,
            OrganizationId = submission.OrganizationId,
            BranchId = submission.BranchId,
            CategoryId = submission.CategoryId,
            ReviewerId = reviewerId,
            Decision = decision,
            Comment = string.IsNullOrEmpty(trimmedComment) ? null : trimmedComment,
            ReworkDueAt = reworkDueAt,
            CreatedAt = now,
        };
    }
}
