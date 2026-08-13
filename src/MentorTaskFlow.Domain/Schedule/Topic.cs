using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Schedule;

/// <summary>
/// One day of the curriculum inside a category (TZ 10.4).
/// </summary>
/// <remarks>
/// A topic carries the plan; the work handed to mentors comes from its
/// <see cref="TopicAssignment"/> templates. Both are scoped to the category, and therefore to exactly
/// one branch of one organization.
/// </remarks>
public sealed class Topic : AuditableEntity
{
    public const int TitleMinLength = 3;
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    /// <summary>Denormalized from the category; composite FK; immutable.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Denormalized from the category; composite FK; immutable.</summary>
    public Guid BranchId { get; private set; }

    public Guid CategoryId { get; private set; }

    /// <summary>Positive, unique within the category.</summary>
    public int DayNumber { get; private set; }

    /// <summary>
    /// Local date of the category. When set, the scheduler picks the topic up on that date.
    /// </summary>
    /// <remarks>
    /// Unique among the <b>active</b> topics of a category, so the scheduler's selection is never
    /// ambiguous (<c>TOPIC-010</c>). A date in the past is deliberately allowed: schedules are
    /// routinely backfilled when a course is rescheduled, and the interface warns rather than the
    /// server refusing (<c>TOPIC-011</c>).
    /// </remarks>
    public DateOnly? PlannedDate { get; private set; }

    public string Title { get; private set; } = null!;

    public string? Description { get; private set; }

    /// <summary>
    /// A deactivated topic drops out of auto-generation and out of the pickers for new assignments,
    /// but stays visible in the schedule marked as archived (<c>TOPIC-013</c>).
    /// </summary>
    public bool IsActive { get; private set; }

    private Topic()
    {
    }

    public static Topic Create(
        Guid organizationId,
        Guid branchId,
        Guid categoryId,
        int dayNumber,
        DateOnly? plannedDate,
        string title,
        string? description,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || branchId == Guid.Empty || categoryId == Guid.Empty)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "OrganizationId, BranchId и CategoryId обязательны и определяются сервером.");
        }

        return new Topic
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            CategoryId = categoryId,
            DayNumber = ValidateDayNumber(dayNumber),
            PlannedDate = plannedDate,
            Title = ValidateTitle(title),
            Description = ValidateDescription(description),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Edits the plan.
    /// </summary>
    /// <remarks>
    /// Moving <see cref="PlannedDate"/> does <b>not</b> shift the deadlines of assignments already
    /// created from this topic: those are absolute UTC moments, and the interface warns about it
    /// before saving (<c>TOPIC-005</c>, <c>CAT-015</c>).
    /// </remarks>
    public void Update(int dayNumber, DateOnly? plannedDate, string title, string? description, DateTimeOffset now)
    {
        DayNumber = ValidateDayNumber(dayNumber);
        PlannedDate = plannedDate;
        Title = ValidateTitle(title);
        Description = ValidateDescription(description);
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        IsActive = true;
        UpdatedAt = now;
    }

    private static int ValidateDayNumber(int dayNumber) =>
        dayNumber > 0
            ? dayNumber
            : throw new DomainException(DomainErrorCodes.ValidationFailed, "Номер дня должен быть больше нуля.");

    private static string ValidateTitle(string title)
    {
        var trimmed = (title ?? string.Empty).Trim();

        return trimmed.Length is >= TitleMinLength and <= TitleMaxLength
            ? trimmed
            : throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Название темы должно содержать от {TitleMinLength} до {TitleMaxLength} символов.");
    }

    private static string? ValidateDescription(string? description)
    {
        var trimmed = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        return trimmed is null || trimmed.Length <= DescriptionMaxLength
            ? trimmed
            : throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Описание темы не должно превышать {DescriptionMaxLength} символов.");
    }
}
