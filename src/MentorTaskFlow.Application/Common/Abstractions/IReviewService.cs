using MentorTaskFlow.Contracts.Reviews;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>The Lead's decision on a submitted version (TZ 15.7).</summary>
public interface IReviewService
{
    /// <summary>
    /// Records a decision and moves the assignment with it.
    /// </summary>
    /// <remarks>
    /// The review, the status change, the new deadline, the event and the notification commit
    /// together (<c>REV-008</c>, <c>ASN-023</c>). Only the latest submission may be reviewed, and only
    /// while the assignment is in <c>InReview</c> — a decision on an old version would leave the
    /// mentor's newest work unjudged (<c>REV-003</c>, <c>REV-004</c>).
    /// </remarks>
    Task<ReviewDto> CreateAsync(Guid submissionId, CreateReviewRequest request, CancellationToken cancellationToken);

    /// <summary>Reads the decision on a submission, or 404 if none has been made (<c>REV-002</c>).</summary>
    Task<ReviewDto> GetAsync(Guid submissionId, CancellationToken cancellationToken);
}
