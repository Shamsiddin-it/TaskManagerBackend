using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Assignments;

/// <summary>
/// A unit of work handed to a mentor (TZ 10.6).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Status"/> has a private setter and moves only through the methods below. Each one
/// checks the transition against the table of 13.3 and throws, so an invalid move is impossible to
/// express rather than merely discouraged (10.6.5). Direct assignment anywhere outside this class is
/// caught by the architecture test <c>TEST-ASN-020</c>.
/// </para>
/// <para>
/// The scope fields are an <b>immutable snapshot</b>. Moving a mentor to another category or branch
/// leaves them untouched: the assignment stays a historical fact of the organization, branch and
/// category it was created in, which is what keeps the analytics of both branches from being
/// rewritten after the fact (10.6.4, <c>TEN-018</c>).
/// </para>
/// </remarks>
public sealed class Assignment : AuditableEntity
{
    public const int TitleMinLength = 3;
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int CancelReasonMinLength = 5;
    public const int CancelReasonMaxLength = 500;

    /// <summary>Null for manual work outside the schedule.</summary>
    public Guid? TopicAssignmentId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid CategoryId { get; private set; }

    /// <summary>An active Mentor of the same organization, branch and category (composite FK, 12.2a).</summary>
    public Guid AssignedToId { get; private set; }

    /// <summary>Null only while an auto-generated suggestion is still unaccepted.</summary>
    public Guid? AssignedById { get; private set; }

    /// <summary>Copied from the template at creation and frozen after publication (10.6.1).</summary>
    public string Title { get; private set; } = null!;

    public string? Description { get; private set; }

    public AssignmentStatus Status { get; private set; }

    public AssignmentSource Source { get; private set; }

    /// <summary>The original deadline. Immutable once the assignment has been published.</summary>
    public DateTimeOffset InitialDueAt { get; private set; }

    /// <summary>
    /// The deadline in force. Equals <see cref="InitialDueAt"/> at creation and changes <b>only</b> on
    /// the transition to <see cref="AssignmentStatus.NeedsRework"/>, taking the review's value.
    /// </summary>
    public DateTimeOffset CurrentDueAt { get; private set; }

    /// <summary>Set only for <see cref="AssignmentSource.Auto"/> (CHECK <c>ck_assignments_auto_fields</c>).</summary>
    public DateOnly? GeneratedForDate { get; private set; }

    /// <summary>Deterministic idempotency key of auto-generation; set only for <c>Auto</c>.</summary>
    public string? AutoGenerationKey { get; private set; }

    public DateTimeOffset? AssignedAt { get; private set; }

    public DateTimeOffset? FirstSubmittedAt { get; private set; }

    /// <summary>Overwritten on each entry into <see cref="AssignmentStatus.InReview"/>.</summary>
    public DateTimeOffset? ReviewStartedAt { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>
    /// The moment of the <b>first</b> overdue transition, never overwritten.
    /// </summary>
    /// <remarks>
    /// A task can go overdue twice — once on its original deadline and again on a rework deadline.
    /// Keeping the first moment is what lets <c>OverdueRate</c> count distinct assignments instead of
    /// events, which would otherwise exceed 100% (14.4, <c>ANA-009</c>).
    /// </remarks>
    public DateTimeOffset? OverdueAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public Guid? CancelledById { get; private set; }

    public string? CancelReason { get; private set; }

    /// <summary>
    /// Counter behind <c>TaskEvent.SequenceNumber</c> (12.4).
    /// </summary>
    /// <remarks>
    /// Held here rather than derived from <c>MAX(sequence_number)</c>: the row is already locked by
    /// the transition, so incrementing a column is both correct and free, while a MAX query would race
    /// with a concurrent writer.
    /// </remarks>
    public int LastEventSequence { get; private set; }

    private Assignment()
    {
    }

    /// <summary>Creates an unpublished draft (<c>ASN-001</c>).</summary>
    public static Assignment CreateDraft(
        Guid organizationId,
        Guid branchId,
        Guid categoryId,
        Guid assignedToId,
        Guid assignedById,
        Guid? topicAssignmentId,
        string title,
        string? description,
        DateTimeOffset initialDueAt,
        DateTimeOffset now) =>
        Create(
            organizationId, branchId, categoryId, assignedToId, assignedById, topicAssignmentId,
            title, description, initialDueAt, AssignmentStatus.Draft, AssignmentSource.Manual,
            generatedForDate: null, autoGenerationKey: null, now);

    /// <summary>
    /// Creates a scheduler suggestion (<c>SCH-004</c>).
    /// </summary>
    /// <remarks>
    /// <c>AssignedById</c> stays null until a Lead accepts it: nobody has yet decided to hand this
    /// work out, and recording the scheduler as the assigner would misattribute the decision.
    /// </remarks>
    public static Assignment CreateSuggestion(
        Guid organizationId,
        Guid branchId,
        Guid categoryId,
        Guid assignedToId,
        Guid topicAssignmentId,
        string title,
        string? description,
        DateTimeOffset initialDueAt,
        DateOnly generatedForDate,
        string autoGenerationKey,
        DateTimeOffset now) =>
        Create(
            organizationId, branchId, categoryId, assignedToId, assignedById: null, topicAssignmentId,
            title, description, initialDueAt, AssignmentStatus.Suggested, AssignmentSource.Auto,
            generatedForDate, autoGenerationKey, now);

    private static Assignment Create(
        Guid organizationId,
        Guid branchId,
        Guid categoryId,
        Guid assignedToId,
        Guid? assignedById,
        Guid? topicAssignmentId,
        string title,
        string? description,
        DateTimeOffset initialDueAt,
        AssignmentStatus status,
        AssignmentSource source,
        DateOnly? generatedForDate,
        string? autoGenerationKey,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || branchId == Guid.Empty || categoryId == Guid.Empty)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "OrganizationId, BranchId и CategoryId обязательны и определяются сервером.");
        }

        if (assignedToId == Guid.Empty)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "Исполнитель обязателен.");
        }

        return new Assignment
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            CategoryId = categoryId,
            AssignedToId = assignedToId,
            AssignedById = assignedById,
            TopicAssignmentId = topicAssignmentId,
            Title = ValidateTitle(title),
            Description = ValidateDescription(description),
            Status = status,
            Source = source,
            InitialDueAt = initialDueAt,

            // Equal at creation; only a rework decision ever moves CurrentDueAt away from it.
            CurrentDueAt = initialDueAt,
            GeneratedForDate = generatedForDate,
            AutoGenerationKey = autoGenerationKey,
            LastEventSequence = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Edits an unpublished assignment (<c>ASN-004</c>).
    /// </summary>
    /// <remarks>
    /// Allowed in <see cref="AssignmentStatus.Draft"/> and <see cref="AssignmentStatus.Suggested"/>
    /// only. Once published, the title, description and initial deadline are what the Mentor was told,
    /// and changing them afterwards would rewrite the terms of work already under way.
    /// </remarks>
    public void Edit(string title, string? description, DateTimeOffset initialDueAt, DateTimeOffset now)
    {
        if (Status is not (AssignmentStatus.Draft or AssignmentStatus.Suggested))
        {
            throw InvalidTransition(Status, Status);
        }

        Title = ValidateTitle(title);
        Description = ValidateDescription(description);
        InitialDueAt = initialDueAt;
        CurrentDueAt = initialDueAt;
        UpdatedAt = now;
    }

    /// <summary>Transition 1: <c>Draft → Assigned</c> (<c>ASN-002</c>).</summary>
    public void Publish(Guid assignedById, DateTimeOffset now)
    {
        Require(AssignmentStatus.Draft, AssignmentStatus.Assigned);

        Status = AssignmentStatus.Assigned;
        AssignedById = assignedById;
        AssignedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Transition 3: <c>Suggested → Assigned</c> (<c>ASN-003</c>).
    /// </summary>
    /// <remarks>
    /// Produces two events sharing one correlation id — <c>SuggestionAccepted</c> then
    /// <c>Assigned</c> — because the Lead's decision and the publication are separate facts a review
    /// may need to tell apart (<c>EVT-005</c>).
    /// </remarks>
    public void AcceptSuggestion(Guid acceptedById, DateTimeOffset now)
    {
        Require(AssignmentStatus.Suggested, AssignmentStatus.Assigned);

        Status = AssignmentStatus.Assigned;
        AssignedById = acceptedById;
        AssignedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Changes the executor without changing the status (10.6.3).
    /// </summary>
    /// <remarks>
    /// Permitted in <c>Draft</c>, <c>Suggested</c> and in <c>Assigned</c> while no submission exists.
    /// After the first submission the executor is fixed for good: reassigning would leave somebody
    /// else's work attached to a task now owned by another person.
    /// </remarks>
    public void Reassign(Guid newAssigneeId, bool hasSubmissions, DateTimeOffset now)
    {
        if (Status is not (AssignmentStatus.Draft or AssignmentStatus.Suggested or AssignmentStatus.Assigned)
            || hasSubmissions)
        {
            throw new DomainException(
                DomainErrorCodes.ReassignNotAllowed,
                "Переназначение недоступно: задача уже сдана или находится в недопустимом статусе.");
        }

        if (newAssigneeId == Guid.Empty)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "Новый исполнитель обязателен.");
        }

        AssignedToId = newAssigneeId;
        UpdatedAt = now;
    }

    /// <summary>
    /// Transitions 5, 13 and 16: a version was uploaded.
    /// </summary>
    /// <remarks>
    /// From <see cref="AssignmentStatus.Overdue"/> this is the machine's only conditional move: the
    /// caller must have checked <c>AllowLateSubmission</c> and answered 409
    /// <c>LATE_SUBMISSION_DISABLED</c> otherwise, before any file reached storage (13.5).
    /// </remarks>
    public void Submit(bool isFirstVersion, DateTimeOffset now)
    {
        if (Status is not (AssignmentStatus.Assigned or AssignmentStatus.NeedsRework or AssignmentStatus.Overdue))
        {
            throw new DomainException(
                DomainErrorCodes.SubmissionNotAllowed,
                "Загрузка работы недоступна в текущем статусе задачи.");
        }

        Status = AssignmentStatus.Submitted;

        if (isFirstVersion)
        {
            FirstSubmittedAt = now;
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// Transition 8: <c>Submitted → InReview</c> (<c>REV-001</c>).
    /// </summary>
    /// <remarks>
    /// Only ever set by this explicit action — never by viewing a page or downloading the file. That
    /// is what keeps <c>FirstReviewResponseTime</c> measuring the real queueing time (13.2,
    /// <c>API-013</c>).
    /// </remarks>
    public void StartReview(DateTimeOffset now)
    {
        Require(AssignmentStatus.Submitted, AssignmentStatus.InReview);

        Status = AssignmentStatus.InReview;
        ReviewStartedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Transition 10: <c>InReview → Approved</c>, terminal.</summary>
    public void Approve(DateTimeOffset now)
    {
        Require(AssignmentStatus.InReview, AssignmentStatus.Approved);

        Status = AssignmentStatus.Approved;
        ApprovedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Transition 11: <c>InReview → NeedsRework</c>.
    /// </summary>
    /// <remarks>
    /// The single point where <see cref="CurrentDueAt"/> moves. <paramref name="reworkDueAt"/> must be
    /// strictly in the future — a rework deadline already past would put the task straight back into
    /// overdue on the next scheduler run (<c>REV-002</c>).
    /// </remarks>
    public void RequestRework(DateTimeOffset reworkDueAt, DateTimeOffset now)
    {
        Require(AssignmentStatus.InReview, AssignmentStatus.NeedsRework);

        if (reworkDueAt <= now)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "Новый дедлайн доработки должен быть в будущем.");
        }

        Status = AssignmentStatus.NeedsRework;
        CurrentDueAt = reworkDueAt;
        UpdatedAt = now;
    }

    /// <summary>
    /// Transitions 6 and 14: the deadline passed while the work was with the Mentor.
    /// </summary>
    /// <remarks>
    /// <see cref="OverdueAt"/> records the first occurrence only. A repeat — a rework deadline missed
    /// after an earlier miss — still raises an event, but overwriting the field would make
    /// <c>OverdueRate</c> count events rather than tasks (14.4).
    /// </remarks>
    public void MarkOverdue(DateTimeOffset now)
    {
        if (Status is not (AssignmentStatus.Assigned or AssignmentStatus.NeedsRework))
        {
            throw InvalidTransition(Status, AssignmentStatus.Overdue);
        }

        Status = AssignmentStatus.Overdue;
        OverdueAt ??= now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Transitions 2, 4, 7, 9, 12, 15 and 17: cancellation from any non-terminal status
    /// (<c>ASN-024</c>).
    /// </summary>
    /// <remarks>
    /// Existing submissions, reviews and events are neither deleted nor altered — they remain the
    /// record of what happened before the task was stopped.
    /// </remarks>
    public void Cancel(Guid cancelledById, string cancelReason, DateTimeOffset now)
    {
        EnsureNotTerminal();

        var trimmed = (cancelReason ?? string.Empty).Trim();

        if (trimmed.Length is < CancelReasonMinLength or > CancelReasonMaxLength)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Причина отмены обязательна и должна содержать от {CancelReasonMinLength} до {CancelReasonMaxLength} символов.");
        }

        Status = AssignmentStatus.Cancelled;
        CancelledById = cancelledById;
        CancelReason = trimmed;
        CancelledAt = now;
        UpdatedAt = now;
    }

    /// <summary>Allocates the next event sequence number under the row lock (12.4).</summary>
    public int NextEventSequence() => ++LastEventSequence;

    /// <summary>True while the assignment still occupies its executor and blocks their transfer.</summary>
    /// <remarks>
    /// The condition <c>USER-012</c> and <c>BRN-038</c> test before allowing a category or branch
    /// change: a task in flight would otherwise stay with somebody no longer in the contour.
    /// </remarks>
    public bool IsInFlight => Status is not (AssignmentStatus.Approved or AssignmentStatus.Cancelled);

    private void Require(AssignmentStatus from, AssignmentStatus to)
    {
        EnsureNotTerminal();

        if (Status != from)
        {
            throw InvalidTransition(Status, to);
        }
    }

    /// <summary>
    /// <c>ASN-021</c>: the terminal code takes precedence over the generic transition error.
    /// </summary>
    /// <remarks>
    /// The distinction matters to the interface: "this task is finished" is a different message from
    /// "that move is not allowed from here", and only the first tells the user to stop trying.
    /// </remarks>
    private void EnsureNotTerminal()
    {
        if (Status is AssignmentStatus.Approved or AssignmentStatus.Cancelled)
        {
            throw new DomainException(
                DomainErrorCodes.AssignmentTerminal,
                "Задача завершена: действия над ней недоступны.",
                new Dictionary<string, object?> { ["currentStatus"] = Status.ToString() });
        }
    }

    private static DomainException InvalidTransition(AssignmentStatus from, AssignmentStatus to) =>
        new(
            DomainErrorCodes.AssignmentInvalidStatusTransition,
            "Переход статуса недопустим.",
            new Dictionary<string, object?>
            {
                ["currentStatus"] = from.ToString(),
                ["requestedStatus"] = to.ToString(),
            });

    private static string ValidateTitle(string title)
    {
        var trimmed = (title ?? string.Empty).Trim();

        return trimmed.Length is >= TitleMinLength and <= TitleMaxLength
            ? trimmed
            : throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Название задачи должно содержать от {TitleMinLength} до {TitleMaxLength} символов.");
    }

    private static string? ValidateDescription(string? description)
    {
        var trimmed = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        return trimmed is null || trimmed.Length <= DescriptionMaxLength
            ? trimmed
            : throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Описание задачи не должно превышать {DescriptionMaxLength} символов.");
    }
}
