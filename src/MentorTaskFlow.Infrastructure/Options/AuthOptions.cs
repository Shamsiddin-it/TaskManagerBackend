using System.ComponentModel.DataAnnotations;

namespace MentorTaskFlow.Infrastructure.Options;

/// <summary>
/// Authentication settings. Single source of truth for the numbers: Приложение L.1.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// JWT signing key, at least 256 bits (<c>AUTH-001</c>). Comes from the secret manager and never
    /// from a repository <c>appsettings.json</c> (<c>SEC-010</c>).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string JwtSigningKey { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string JwtIssuer { get; init; } = "mentortaskflow";

    [Required(AllowEmptyStrings = false)]
    public string JwtAudience { get; init; } = "mentortaskflow-api";

    /// <summary><c>AUTH__ACCESS_TOKEN_MINUTES</c>, default 15 (<c>AUTH-001</c>).</summary>
    [Range(1, 60)]
    public int AccessTokenMinutes { get; init; } = 15;

    /// <summary><c>AUTH__REFRESH_TOKEN_DAYS</c>, default 14 (<c>AUTH-005</c>).</summary>
    [Range(1, 90)]
    public int RefreshTokenDays { get; init; } = 14;

    /// <summary><c>AUTH__CLOCK_SKEW_SECONDS</c>, default 30 (<c>AUTH-004</c>).</summary>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; init; } = 30;

    /// <summary>
    /// <c>AUTH__TOKEN_VERSION_CACHE_SECONDS</c>, default 30 (<c>AUTH-028</c>). This value <b>is</b> the
    /// revocation window the system promises, so raising it weakens the guarantee of section 16.7.
    /// </summary>
    [Range(0, 300)]
    public int TokenVersionCacheSeconds { get; init; } = 30;

    /// <summary><c>AUTH__SET_PASSWORD_TOKEN_HOURS</c>, default 24 (<c>AUTH-020</c>).</summary>
    [Range(1, 168)]
    public int SetPasswordTokenHours { get; init; } = 24;

    /// <summary><c>AUTH__RESET_PASSWORD_TOKEN_MINUTES</c>, default 30 (<c>AUTH-016</c>).</summary>
    [Range(1, 1440)]
    public int ResetPasswordTokenMinutes { get; init; } = 30;

    /// <summary><c>AUTH__LOCKOUT_ATTEMPTS</c>, default 5 (<c>AUTH-024</c>).</summary>
    [Range(1, 20)]
    public int LockoutAttempts { get; init; } = 5;

    /// <summary><c>AUTH__LOCKOUT_MINUTES</c>, default 15 (<c>AUTH-024</c>).</summary>
    [Range(1, 1440)]
    public int LockoutMinutes { get; init; } = 15;

    /// <summary>
    /// Base address of the SPA, used to build set-password and reset-password links
    /// (<c>https://app.&lt;domain&gt;</c>, <c>DEPLOY-010</c>).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string AppBaseUrl { get; init; } = "http://localhost:5173";

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(AccessTokenMinutes);

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(RefreshTokenDays);

    public TimeSpan SetPasswordTokenLifetime => TimeSpan.FromHours(SetPasswordTokenHours);

    public TimeSpan ResetPasswordTokenLifetime => TimeSpan.FromMinutes(ResetPasswordTokenMinutes);

    public TimeSpan LockoutDuration => TimeSpan.FromMinutes(LockoutMinutes);
}
