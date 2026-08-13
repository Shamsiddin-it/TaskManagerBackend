using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Schedule;

/// <summary>Kind of work a template produces (TZ 10.5).</summary>
public enum TopicAssignmentType
{
    Presentation = 0,
    ClassTask = 1,
    HomeTask = 2,
}

/// <summary>
/// A template for the work handed to a mentor on a given topic (TZ 10.5).
/// </summary>
/// <remarks>
/// <para>
/// A template is not the task. When an assignment is created, <see cref="Title"/> and
/// <see cref="Description"/> are <b>copied</b> into it, and later edits to the template leave existing
/// assignments untouched — the snapshot rule of 10.6.1 (<c>TPL-004</c>).
/// </para>
/// <para>
/// Scope is inherited from the topic and never supplied by a client. The composite FK
/// <c>fk_topic_assignments_topic_scope</c> makes a template attached to a topic of another branch
/// physically impossible (<c>TPL-005</c>).
/// </para>
/// </remarks>
public sealed class TopicAssignment : AuditableEntity
{
    public const int TitleMinLength = 3;
    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    public Guid TopicId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid CategoryId { get; private set; }

    public TopicAssignmentType Type { get; private set; }

    public string Title { get; private set; } = null!;

    public string? Description { get; private set; }

    /// <summary>
    /// Only required templates are turned into suggestions by the scheduler (<c>SCH-003</c>).
    /// </summary>
    public bool IsRequired { get; private set; }

    /// <summary>
    /// Archiving instead of deletion. A deactivated template stops feeding auto-generation and stops
    /// appearing when creating new assignments; assignments already created are untouched
    /// (<c>TPL-003</c>).
    /// </summary>
    public bool IsActive { get; private set; }

    private TopicAssignment()
    {
    }

    /// <summary>
    /// Creates a template inside a topic, inheriting its scope.
    /// </summary>
    /// <remarks>
    /// The scope comes from the topic entity rather than from the request, which is what makes
    /// <c>TPL-001</c> enforceable: a client has no way to name a different organization, branch or
    /// category.
    /// </remarks>
    public static TopicAssignment Create(
        Topic topic,
        TopicAssignmentType type,
        string title,
        string? description,
        bool isRequired,
        DateTimeOffset now) =>
        new()
        {
            TopicId = topic.Id,
            OrganizationId = topic.OrganizationId,
            BranchId = topic.BranchId,
            CategoryId = topic.CategoryId,
            Type = type,
            Title = ValidateTitle(title),
            Description = ValidateDescription(description),
            IsRequired = isRequired,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public void Update(TopicAssignmentType type, string title, string? description, bool isRequired, DateTimeOffset now)
    {
        Type = type;
        Title = ValidateTitle(title);
        Description = ValidateDescription(description);
        IsRequired = isRequired;
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

    private static string ValidateTitle(string title)
    {
        var trimmed = (title ?? string.Empty).Trim();

        return trimmed.Length is >= TitleMinLength and <= TitleMaxLength
            ? trimmed
            : throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Название задания должно содержать от {TitleMinLength} до {TitleMaxLength} символов.");
    }

    private static string? ValidateDescription(string? description)
    {
        var trimmed = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        return trimmed is null || trimmed.Length <= DescriptionMaxLength
            ? trimmed
            : throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Описание задания не должно превышать {DescriptionMaxLength} символов.");
    }
}
