using MentorTaskFlow.Contracts.Tenancy;

namespace MentorTaskFlow.Contracts.Assignments;

/// <summary>An assignment as returned by the API (TZ 10.6).</summary>
public sealed record AssignmentDto(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    Guid CategoryId,
    Guid? TopicAssignmentId,
    Guid AssignedToId,
    Guid? AssignedById,
    string Title,
    string? Description,
    string Status,
    string Source,
    DateTimeOffset InitialDueAt,
    DateTimeOffset CurrentDueAt,
    DateOnly? GeneratedForDate,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? FirstSubmittedAt,
    DateTimeOffset? ReviewStartedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? OverdueAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    DateTimeOffset CreatedAt,
    string ConcurrencyToken,
    BranchSummaryDto? Branch);

/// <summary>
/// One entry of the assignment's history (TZ 10.9, Приложение F).
/// </summary>
/// <remarks>
/// For a Mentor, <c>actorId</c> is replaced by the actor's role — <c>Lead</c> or <c>Система</c> — so
/// the history stays readable without exposing who else touched the task (<c>EVT-004</c>).
/// </remarks>
public sealed record TaskEventDto(
    Guid Id,
    int SequenceNumber,
    string EventType,
    Guid? ActorId,
    string? ActorLabel,
    string? PreviousStatus,
    string? NewStatus,
    DateTimeOffset OccurredAt,
    Guid CorrelationId);

/// <summary>
/// <c>POST /assignments/drafts</c> (<c>ASN-001</c>).
/// </summary>
/// <remarks>
/// <para>
/// No scope fields and no <c>assignedById</c>: the Lead is taken from the token, and the
/// organization, branch and category from their own category (<c>SEC-003</c>).
/// </para>
/// <para>
/// <c>dueAt</c> is optional. Omitted, the deadline falls on the category's
/// <c>DefaultDueTimeLocal</c> that many days ahead; a date without a time of day gets the same
/// default time (<c>ASN-027</c>).
/// </para>
/// </remarks>
public sealed record CreateAssignmentDraftRequest(
    Guid AssignedToId,
    Guid? TopicAssignmentId,
    string? Title,
    string? Description,
    DateTimeOffset? DueAt);

/// <summary>
/// <c>PUT /assignments/{id}</c> — allowed in <c>Draft</c> and <c>Suggested</c> only (<c>ASN-004</c>).
/// </summary>
public sealed record UpdateAssignmentRequest(
    Guid AssignedToId,
    string Title,
    string? Description,
    DateTimeOffset DueAt,
    string ConcurrencyToken);

/// <summary><c>POST /assignments/{id}/reassign</c> (<c>ASN-005</c>).</summary>
public sealed record ReassignAssignmentRequest(Guid AssignedToId, string ConcurrencyToken, string? Reason = null);

/// <summary>
/// <c>POST /assignments/{id}/cancel</c> (<c>ASN-006</c>).
/// </summary>
/// <remarks>
/// The reason is mandatory, 5–500 characters. For an administrator this is a force-cancel — the one
/// action either administrative contour may take inside the study cycle — and the reason is what
/// makes it accountable (<c>ASN-025</c>).
/// </remarks>
public sealed record CancelAssignmentRequest(string CancelReason, string ConcurrencyToken);

/// <summary>Body of the remaining state-changing actions (<c>API-016</c>).</summary>
public sealed record AssignmentActionRequest(string ConcurrencyToken);

/// <summary>Filters for <c>GET /assignments</c> (<c>ASN-009</c>).</summary>
public sealed record AssignmentListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    public string? Status { get; init; }

    public Guid? CategoryId { get; init; }

    /// <summary>
    /// Ignored for a Mentor, who only ever sees their own tasks.
    /// </summary>
    /// <remarks>
    /// Not an error but a silent narrowing: the ownership condition is applied regardless, so the
    /// filter cannot widen the result beyond what level 4 of TZ 9.1 permits.
    /// </remarks>
    public Guid? AssignedToId { get; init; }

    public string? Source { get; init; }

    /// <summary>Whitelisted: <c>currentDueAt</c>, <c>createdAt</c>, <c>status</c> (<c>API-004</c>).</summary>
    public string? Sort { get; init; }

    public string? Order { get; init; }
}
