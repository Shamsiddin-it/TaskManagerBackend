using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Categories;

/// <summary>
/// Per-category settings: time zone, deadlines, reminders, late-submission policy (TZ 10.3).
/// </summary>
/// <remarks>
/// The row is created in the same transaction as its <see cref="Category"/>; a category without
/// settings does not exist (<c>CAT-014</c>). The primary key <b>is</b> the category id.
/// </remarks>
public sealed class CategorySettings
{
    public const int MinDueDays = 1;
    public const int MaxDueDays = 60;
    public const int MinReminderHours = 1;
    public const int MaxReminderHours = 168;

    public const int DefaultDueDays = 3;
    public const int DefaultReminderHours = 24;

    /// <summary>The default when the branch supplies no value (Приложение L.4).</summary>
    public const string FallbackTimeZoneId = "Asia/Dushanbe";

    /// <summary>PK and FK at once.</summary>
    public Guid CategoryId { get; private set; }

    /// <summary>Denormalized from the category; composite FK; immutable.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Denormalized from the category; composite FK; immutable.</summary>
    public Guid BranchId { get; private set; }

    /// <summary>
    /// Initialised from <c>Branch.TimeZoneId</c> at creation (<c>CAT-023</c>). A later change to the
    /// branch time zone does <b>not</b> propagate here: shifting the deadline arithmetic of live
    /// categories retroactively is exactly what <c>BRN-029</c> forbids.
    /// </summary>
    public string TimeZoneId { get; private set; } = null!;

    public int DefaultAssignmentDueDays { get; private set; }

    /// <summary>
    /// Local time of day for a deadline, default <c>23:59</c>. Introduced in 2.1 because
    /// «PlannedDate + DueDays» left the time of day undefined (<c>CAT-017</c>, TZ 14.2).
    /// </summary>
    public TimeOnly DefaultDueTimeLocal { get; private set; }

    /// <summary>1–168. Zero is rejected by CHECK: disabling reminders is not supported (<c>NTF-007</c>).</summary>
    public int DeadlineReminderHours { get; private set; }

    /// <summary>
    /// Read at the moment of upload, not at assignment creation, so a change takes effect immediately
    /// on existing assignments (<c>CAT-016</c>, <c>SUB-023</c>).
    /// </summary>
    public bool AllowLateSubmission { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private CategorySettings()
    {
    }

    /// <summary>Creates the defaults that accompany a new category, inheriting the branch time zone.</summary>
    public static CategorySettings CreateDefault(Category category, string branchTimeZoneId, DateTimeOffset now) =>
        new()
        {
            CategoryId = category.Id,
            OrganizationId = category.OrganizationId,
            BranchId = category.BranchId,
            TimeZoneId = string.IsNullOrWhiteSpace(branchTimeZoneId) ? FallbackTimeZoneId : branchTimeZoneId,
            DefaultAssignmentDueDays = DefaultDueDays,
            DefaultDueTimeLocal = new TimeOnly(23, 59),
            DeadlineReminderHours = DefaultReminderHours,
            AllowLateSubmission = true,
            UpdatedAt = now,
        };

    /// <summary>
    /// Applies new settings. Deadlines of already-published assignments are <b>not</b> recomputed:
    /// they are stored as absolute UTC moments, and the new values apply only to assignments created
    /// afterwards (<c>CAT-015</c>).
    /// </summary>
    public void Update(
        string timeZoneId,
        int defaultAssignmentDueDays,
        TimeOnly defaultDueTimeLocal,
        int deadlineReminderHours,
        bool allowLateSubmission,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "TimeZoneId обязателен.");
        }

        if (defaultAssignmentDueDays is < MinDueDays or > MaxDueDays)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"DefaultAssignmentDueDays должен быть в диапазоне {MinDueDays}–{MaxDueDays}.");
        }

        if (deadlineReminderHours is < MinReminderHours or > MaxReminderHours)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"DeadlineReminderHours должен быть в диапазоне {MinReminderHours}–{MaxReminderHours}.");
        }

        TimeZoneId = timeZoneId;
        DefaultAssignmentDueDays = defaultAssignmentDueDays;
        DefaultDueTimeLocal = defaultDueTimeLocal;
        DeadlineReminderHours = deadlineReminderHours;
        AllowLateSubmission = allowLateSubmission;
        UpdatedAt = now;
    }
}
