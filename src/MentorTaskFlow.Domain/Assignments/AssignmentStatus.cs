namespace MentorTaskFlow.Domain.Assignments;

/// <summary>
/// The nine statuses of the assignment state machine (TZ 13.1).
/// </summary>
/// <remarks>
/// Section 13 is the single source of truth for statuses and transitions; the transition table of
/// 13.3, Приложение B and Приложение I describe one model, and any divergence between them is a
/// defect in the document.
/// </remarks>
public enum AssignmentStatus
{
    /// <summary>Created by a Lead by hand, unpublished. Visible to the Lead and Admin only; no notifications.</summary>
    Draft = 0,

    /// <summary>Produced by the scheduler. Visible to the Lead and Admin; the Mentor does not see it.</summary>
    Suggested = 1,

    /// <summary>Published and visible to the Mentor. <c>CurrentDueAt</c> is in force.</summary>
    Assigned = 2,

    /// <summary>
    /// A version has been uploaded and is <b>queued</b> for review; the Lead has not started looking.
    /// </summary>
    /// <remarks>
    /// The split from <see cref="InReview"/> is deliberate: <c>FirstReviewResponseTime</c> measures
    /// exactly this queueing time, so a status that changed on page view would corrupt the metric.
    /// </remarks>
    Submitted = 3,

    /// <summary>
    /// The Lead started the review by an explicit action, never by opening a page or downloading a
    /// file (13.2).
    /// </summary>
    InReview = 4,

    /// <summary>Returned for rework. <c>CurrentDueAt</c> now holds the review's <c>ReworkDueAt</c>.</summary>
    NeedsRework = 5,

    /// <summary>
    /// The deadline passed while the work was with the Mentor.
    /// </summary>
    /// <remarks>
    /// Reachable only from <see cref="Assigned"/> and <see cref="NeedsRework"/>. <see cref="Submitted"/>
    /// and <see cref="InReview"/> never go overdue — the work is already handed in and sitting with the
    /// Lead, whose slowness is tracked by a metric rather than by the task's status (<c>SCH-022</c>).
    /// </remarks>
    Overdue = 6,

    /// <summary>Terminal. No upload, edit, repeat review or cancellation is possible.</summary>
    Approved = 7,

    /// <summary>Terminal. Existing submissions, reviews and events survive and stay readable.</summary>
    Cancelled = 8,
}

/// <summary>How the assignment came into being (TZ 10.6).</summary>
public enum AssignmentSource
{
    /// <summary>Produced by the scheduler; carries <c>GeneratedForDate</c> and <c>AutoGenerationKey</c>.</summary>
    Auto = 0,

    /// <summary>Created by a Lead; both auto-generation fields are null (<c>SCH-012</c>).</summary>
    Manual = 1,
}
