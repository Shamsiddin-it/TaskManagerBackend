namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>
/// Validates IANA time-zone identifiers against the tzdata shipped with the host (<c>BRN-010</c>).
/// </summary>
/// <remarks>
/// <para>
/// Checked on <b>every</b> save of <c>Branch.TimeZoneId</c> and <c>CategorySettings.TimeZoneId</c>. An
/// unknown zone accepted here would surface much later as a wrong deadline: the whole of TZ 14.2 and
/// the scheduler of 20.1 resolve local time through this identifier.
/// </para>
/// <para>
/// The identifier must be IANA (<c>Asia/Dushanbe</c>), never a Windows display name. Linux containers
/// are mandated for exactly this reason (<c>DEPLOY-011</c>), and the Dockerfile verifies tzdata is
/// present rather than assuming it.
/// </para>
/// </remarks>
public interface ITimeZoneCatalog
{
    bool Exists(string? timeZoneId);
}
