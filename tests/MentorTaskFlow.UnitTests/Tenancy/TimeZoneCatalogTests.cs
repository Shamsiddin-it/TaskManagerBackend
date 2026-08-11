using MentorTaskFlow.Infrastructure.Common;

namespace MentorTaskFlow.UnitTests.Tenancy;

/// <summary><c>BRN-010</c>: only IANA identifiers present in tzdata are accepted.</summary>
public sealed class TimeZoneCatalogTests
{
    private readonly TimeZoneCatalog _catalog = new();

    [Theory]
    [InlineData("Asia/Dushanbe")]
    [InlineData("Europe/Moscow")]
    [InlineData("UTC")]
    public void A_known_iana_zone_is_accepted(string timeZoneId)
    {
        _catalog.Exists(timeZoneId).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Asia/Atlantis")]
    [InlineData("Not A Zone")]
    public void An_unknown_value_is_rejected(string? timeZoneId)
    {
        _catalog.Exists(timeZoneId).ShouldBeFalse();
    }

    /// <summary>
    /// A Windows display name resolves on Windows through the ICU mapping but does not exist on the
    /// Linux containers the system is deployed to (<c>DEPLOY-011</c>). Accepting it here would store a
    /// value that later fails to resolve and silently drops deadline arithmetic back to UTC.
    /// </summary>
    [Theory]
    [InlineData("Central Asia Standard Time")]
    [InlineData("Russian Standard Time")]
    public void A_windows_identifier_is_rejected(string timeZoneId)
    {
        _catalog.Exists(timeZoneId).ShouldBeFalse();
    }
}
