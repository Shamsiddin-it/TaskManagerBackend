namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>
/// Turns a local date and time of day in a category's zone into an absolute UTC deadline
/// (TZ 14.2, 14.3).
/// </summary>
/// <remarks>
/// <para>
/// Version 2.0 defined the deadline as «PlannedDate + DueDays» and left the time of day undefined,
/// which made two implementations disagree by up to a day. It is now
/// <c>ConvertToUtc(localDate + DueDays at DefaultDueTimeLocal, TimeZoneId)</c> (<c>SCH-020</c>).
/// </para>
/// <para>
/// Everything is stored in UTC. Conversion to the category's zone happens at the presentation
/// boundary — an API response or a notification template — and never in storage (<c>DEPLOY-002</c>).
/// </para>
/// </remarks>
public interface IDeadlineCalculator
{
    /// <summary>
    /// Resolves a local moment to UTC, applying the two daylight-saving rules of 14.3.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>
    ///     The local time <b>does not exist</b> (clocks jumped forward): the first moment after the
    ///     gap is used. A deadline must not vanish because of a clock change.
    ///   </description></item>
    ///   <item><description>
    ///     The local time is <b>ambiguous</b> (clocks went back, the hour repeats): the <b>later</b>
    ///     of the two instants is used, which gives the mentor more time rather than less.
    ///   </description></item>
    /// </list>
    /// </remarks>
    DateTimeOffset ToUtc(DateOnly localDate, TimeOnly localTime, string timeZoneId);

    /// <summary>Computes the deadline of a scheduled assignment (<c>SCH-020</c>).</summary>
    DateTimeOffset CalculateInitialDueAt(DateOnly plannedDate, int dueDays, TimeOnly dueTimeLocal, string timeZoneId);
}
