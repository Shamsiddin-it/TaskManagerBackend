using System.Text.Json;
using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Assignments;

/// <summary>The twelve event types of Приложение F.</summary>
/// <remarks>
/// Version 2.0's <c>DueDateChanged</c> was removed: no Release 1.0 action produced it. A deadline
/// change is recorded by the <c>previousCurrentDueAt</c> and <c>newCurrentDueAt</c> fields of
/// <see cref="ReviewNeedsRework"/>, where it actually happens.
/// </remarks>
public enum TaskEventType
{
    DraftCreated = 0,
    SuggestedCreated = 1,
    SuggestionAccepted = 2,
    Assigned = 3,
    Reassigned = 4,
    SubmissionUploaded = 5,
    LateSubmissionUploaded = 6,
    ReviewStarted = 7,
    ReviewApproved = 8,
    ReviewNeedsRework = 9,
    MarkedOverdue = 10,
    Cancelled = 11,
}

/// <summary>
/// Append-only journal of one assignment's history (TZ 10.9).
/// </summary>
/// <remarks>
/// <para>
/// Written in the <b>same transaction</b> as the change it describes; a status change without its
/// event is a defect (<c>EVT-002</c>). <c>UPDATE</c> and <c>DELETE</c> are revoked from the
/// application role, so append-only is a database guarantee (<c>EVT-001</c>, TZ 12.6).
/// </para>
/// <para>
/// The source of truth for <b>timing and transitions</b> only. Assignment, Submission and Review
/// remain the source for subject data, and metrics are never reconstructed from events alone
/// (<c>EVT-003</c>).
/// </para>
/// </remarks>
public sealed class TaskEvent : BaseEntity
{
    public Guid AssignmentId { get; private set; }

    /// <summary>
    /// Must equal the assignment's scope byte for byte, enforced by
    /// <c>fk_task_events_assignment_scope</c> (<c>EVT-007</c>).
    /// </summary>
    public Guid OrganizationId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid CategoryId { get; private set; }

    /// <summary>Unique within the assignment, rising from 1 (12.4).</summary>
    public int SequenceNumber { get; private set; }

    public TaskEventType EventType { get; private set; }

    /// <summary>Null only for system events — <c>MarkedOverdue</c> and <c>SuggestedCreated</c>.</summary>
    public Guid? ActorId { get; private set; }

    /// <summary>Null only for creation events.</summary>
    public AssignmentStatus? PreviousStatus { get; private set; }

    /// <summary>Null for events that change no status, such as <c>Reassigned</c>.</summary>
    public AssignmentStatus? NewStatus { get; private set; }

    public Guid? SubmissionId { get; private set; }

    public Guid? ReviewId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>
    /// Ties the API action, the assignment change, this event and the notifications together.
    /// </summary>
    /// <remarks>
    /// One business transition may produce more than one event — accepting a suggestion produces two
    /// — and they share this id and take consecutive sequence numbers (<c>EVT-005</c>).
    /// </remarks>
    public Guid CorrelationId { get; private set; }

    /// <summary>
    /// Typed per event type (Приложение F).
    /// </summary>
    /// <remarks>
    /// Never holds passwords, tokens, presigned URLs, file contents or third-party file names
    /// (<c>SEC-021</c>).
    /// </remarks>
    public JsonDocument? Metadata { get; private set; }

    public int MetadataSchemaVersion { get; private set; }

    private TaskEvent()
    {
    }

    /// <summary>
    /// Records an event, taking its scope and sequence from the assignment itself.
    /// </summary>
    /// <remarks>
    /// The scope is copied from the aggregate rather than passed in, so an event with a scope
    /// different from its assignment cannot be constructed — the composite FK would refuse it anyway,
    /// and this makes the attempt impossible one layer earlier (<c>EVT-007</c>).
    /// </remarks>
    public static TaskEvent Record(
        Assignment assignment,
        TaskEventType eventType,
        Guid? actorId,
        AssignmentStatus? previousStatus,
        AssignmentStatus? newStatus,
        Guid correlationId,
        DateTimeOffset now,
        JsonDocument? metadata = null,
        Guid? submissionId = null,
        Guid? reviewId = null)
    {
        if (eventType is TaskEventType.MarkedOverdue or TaskEventType.SuggestedCreated && actorId is not null)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Событие {eventType} является системным и не может иметь ActorId (10.9).");
        }

        return new TaskEvent
        {
            AssignmentId = assignment.Id,
            OrganizationId = assignment.OrganizationId,
            BranchId = assignment.BranchId,
            CategoryId = assignment.CategoryId,
            SequenceNumber = assignment.NextEventSequence(),
            EventType = eventType,
            ActorId = actorId,
            PreviousStatus = previousStatus,
            NewStatus = newStatus,
            SubmissionId = submissionId,
            ReviewId = reviewId,
            OccurredAt = now,
            CorrelationId = correlationId,
            Metadata = metadata,
            MetadataSchemaVersion = 1,
            CreatedAt = now,
        };
    }
}
