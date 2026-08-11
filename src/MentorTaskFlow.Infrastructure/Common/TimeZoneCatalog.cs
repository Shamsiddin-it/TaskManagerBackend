using MentorTaskFlow.Application.Common.Abstractions;

namespace MentorTaskFlow.Infrastructure.Common;

/// <inheritdoc />
public sealed class TimeZoneCatalog : ITimeZoneCatalog
{
    public bool Exists(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        // A Windows identifier such as "Central Asia Standard Time" resolves on Windows through the
        // ICU mapping, so an explicit shape check comes first: the value is persisted and later read
        // by Linux containers, where only the IANA form exists (DEPLOY-011).
        if (!timeZoneId.Contains('/') && !string.Equals(timeZoneId, "UTC", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            // The zone exists in tzdata but its data is unusable — treat it as unknown rather than
            // letting a corrupt entry through to deadline arithmetic.
            return false;
        }
    }
}
