using MentorTaskFlow.Contracts.Assignments;
using MentorTaskFlow.Contracts.Common;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>The assignment lifecycle (TZ 13, 15.5, Приложение D.5).</summary>
public interface IAssignmentService
{
    /// <summary>
    /// Lists assignments within the caller's reach.
    /// </summary>
    /// <remarks>
    /// A Mentor sees only their own; a Lead their category; a Branch Admin their branch; an
    /// Organization Admin the organization. Drafts and suggestions stay invisible to the Mentor — a
    /// suggestion is a proposal the Lead has not yet accepted, and showing it would announce work
    /// that may never be handed out (13.2).
    /// </remarks>
    Task<PagedResult<AssignmentDto>> ListAsync(AssignmentListQuery query, CancellationToken cancellationToken);

    Task<AssignmentDto> GetAsync(Guid assignmentId, CancellationToken cancellationToken);

    /// <summary>The history in sequence order (<c>ASN-009</c>).</summary>
    Task<IReadOnlyList<TaskEventDto>> GetHistoryAsync(Guid assignmentId, CancellationToken cancellationToken);

    Task<AssignmentDto> CreateDraftAsync(CreateAssignmentDraftRequest request, CancellationToken cancellationToken);

    Task<AssignmentDto> UpdateAsync(Guid assignmentId, UpdateAssignmentRequest request, CancellationToken cancellationToken);

    /// <summary>Transition 1: <c>Draft → Assigned</c> (<c>ASN-002</c>).</summary>
    Task<AssignmentDto> PublishAsync(Guid assignmentId, AssignmentActionRequest request, CancellationToken cancellationToken);

    /// <summary>Transition 3: <c>Suggested → Assigned</c> (<c>ASN-003</c>).</summary>
    Task<AssignmentDto> AcceptSuggestionAsync(Guid assignmentId, AssignmentActionRequest request, CancellationToken cancellationToken);

    Task<AssignmentDto> ReassignAsync(Guid assignmentId, ReassignAssignmentRequest request, CancellationToken cancellationToken);

    /// <summary>Transition 8: <c>Submitted → InReview</c> (<c>REV-001</c>).</summary>
    Task<AssignmentDto> StartReviewAsync(Guid assignmentId, AssignmentActionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Cancellation from any non-terminal status (<c>ASN-006</c>).
    /// </summary>
    /// <remarks>
    /// Available to the Lead of the category and, as a force-cancel with a reason, to either
    /// administrative contour within its own reach. It is the single administrative action inside the
    /// study cycle, meant to unblock stuck work (<c>ASN-025</c>).
    /// </remarks>
    Task<AssignmentDto> CancelAsync(Guid assignmentId, CancelAssignmentRequest request, CancellationToken cancellationToken);
}
