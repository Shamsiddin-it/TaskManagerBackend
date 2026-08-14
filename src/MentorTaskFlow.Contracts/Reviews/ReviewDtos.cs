namespace MentorTaskFlow.Contracts.Reviews;

/// <summary>
/// <c>POST /submissions/{id}/reviews</c> (<c>REV-002</c>).
/// </summary>
/// <remarks>
/// <para>
/// No <c>reviewerId</c>: it comes from the token on the server, and sending one is 400
/// <c>VALIDATION_FAILED</c> rather than being ignored (<c>REV-006</c>).
/// </para>
/// <para>
/// <c>concurrencyToken</c> is the <b>assignment's</b>, not the submission's. A submission is
/// append-only and carries no token; what the decision changes is the assignment's status and its
/// deadline, so that is the row a concurrent writer would collide with.
/// </para>
/// </remarks>
public sealed record CreateReviewRequest(
    string Decision,
    string ConcurrencyToken,
    string? Comment = null,
    DateTimeOffset? ReworkDueAt = null);

/// <summary>A recorded decision (<c>REV-020</c>: it is never edited, so there is no token here either).</summary>
public sealed record ReviewDto(
    Guid Id,
    Guid SubmissionId,
    Guid AssignmentId,
    Guid ReviewerId,
    string Decision,
    string? Comment,
    DateTimeOffset? ReworkDueAt,
    DateTimeOffset CreatedAt);
