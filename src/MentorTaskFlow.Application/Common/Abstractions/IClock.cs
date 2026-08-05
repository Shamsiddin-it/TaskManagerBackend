namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>
/// The only source of current time in Application and Domain code.
/// </summary>
/// <remarks>
/// Direct use of <c>DateTimeOffset.UtcNow</c> outside Infrastructure is rejected by
/// <c>TEST-SEC-024</c>: deadline, DST and overdue logic (TZ 14.2–14.5) is not testable when the
/// clock is ambient. All values are UTC (<c>DEPLOY-002</c>).
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
