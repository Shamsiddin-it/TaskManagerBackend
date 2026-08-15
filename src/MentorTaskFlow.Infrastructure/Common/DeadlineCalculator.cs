using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using Microsoft.Extensions.Logging;

namespace MentorTaskFlow.Infrastructure.Common;

/// <inheritdoc />
public sealed class DeadlineCalculator(ILogger<DeadlineCalculator> logger) : IDeadlineCalculator
{
    public DateTimeOffset CalculateInitialDueAt(
        DateOnly plannedDate,
        int dueDays,
        TimeOnly dueTimeLocal,
        string timeZoneId) =>
        ToUtc(plannedDate.AddDays(dueDays), dueTimeLocal, timeZoneId);

    public DateTimeOffset ToLocal(DateTimeOffset instant, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(instant, FindZone(timeZoneId));

    public DateTimeOffset ToUtc(DateOnly localDate, TimeOnly localTime, string timeZoneId)
    {
        var zone = FindZone(timeZoneId);
        var local = localDate.ToDateTime(localTime);

        // Clocks jumped forward and this wall-clock time never happens. Walking forward to the first
        // moment after the gap keeps the deadline in existence; refusing or silently shifting it back
        // would either break the save or quietly move the deadline earlier (14.3).
        if (zone.IsInvalidTime(local))
        {
            var resolved = ResolveGap(local, zone);

            // Information, not Warning: this is expected behaviour of a correct calendar, and the log
            // exists so the shift can be explained to a user who notices it (SCH-021).
            logger.LogInformation(
                "Local time {LocalTime} does not exist in {TimeZoneId} (daylight-saving gap); using {Resolved:O}.",
                local,
                timeZoneId,
                resolved);

            return resolved;
        }

        // Clocks went back and this wall-clock time happens twice. The later instant is chosen because
        // it gives the mentor more time, which is the safer product outcome (14.3).
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var latest = offsets.Min();

            var resolved = new DateTimeOffset(local, latest).ToUniversalTime();

            logger.LogInformation(
                "Local time {LocalTime} is ambiguous in {TimeZoneId} (daylight-saving overlap); using the later instant {Resolved:O}.",
                local,
                timeZoneId,
                resolved);

            return resolved;
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>
    /// Walks forward minute by minute to the first wall-clock time that exists.
    /// </summary>
    /// <remarks>
    /// Adding the transition delta directly would assume it is always one hour; real zones have used
    /// 30- and 45-minute shifts, and Lord Howe still uses 30 minutes. Stepping avoids encoding that
    /// assumption, and a gap is bounded by a couple of hours so the loop is trivially short.
    /// </remarks>
    private static DateTimeOffset ResolveGap(DateTime local, TimeZoneInfo zone)
    {
        var candidate = local;

        for (var minutes = 0; minutes < 24 * 60; minutes++)
        {
            candidate = local.AddMinutes(minutes);

            if (!zone.IsInvalidTime(candidate))
            {
                return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate)).ToUniversalTime();
            }
        }

        throw new InvalidOperationException(
            $"Could not resolve local time {local:O} in {zone.Id}: no valid moment within 24 hours.");
    }

    private static TimeZoneInfo FindZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // The zone is validated on every save of Branch and CategorySettings (BRN-010), so reaching
            // this means the tzdata of the running host differs from the one that accepted the value.
            throw new ValidationAppException(
                "timeZoneId",
                $"Часовой пояс '{timeZoneId}' отсутствует в базе tzdata на этом узле.");
        }
    }
}
