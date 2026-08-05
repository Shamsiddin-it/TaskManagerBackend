using System.Security.Cryptography;

namespace MentorTaskFlow.Api.Authentication;

/// <summary>
/// Writes and clears the two authentication cookies (TZ 16.3).
/// </summary>
/// <remarks>
/// <para>
/// Version 2.0 allowed <c>SameSite=Strict</c> together with cross-site hosting, which are mutually
/// exclusive. One topology is fixed instead: the SPA on <c>app.&lt;domain&gt;</c> and the API on
/// <c>api.&lt;domain&gt;</c>, sharing a registrable domain. Those are same-site, so a
/// <c>SameSite=Strict</c> cookie is still sent on the XHR to <c>/api/v1/auth/refresh</c>
/// (<c>DEPLOY-010</c>, <c>AUTH-010</c>).
/// </para>
/// <para>
/// No <c>Domain</c> attribute is set, making both cookies host-only: a sibling subdomain cannot
/// receive them.
/// </para>
/// </remarks>
public sealed class AuthCookieManager
{
    /// <summary>Refresh token. HttpOnly, so page script cannot read it.</summary>
    public const string RefreshTokenCookieName = "mtf_rt";

    /// <summary>CSRF token. Deliberately readable by script — the client must echo it in a header.</summary>
    public const string CsrfCookieName = "mtf_csrf";

    public const string CsrfHeaderName = "X-CSRF-Token";

    /// <summary>
    /// Restricts both cookies to the auth endpoints, so no other request carries them
    /// (<c>AUTH-010</c>, <c>AUTH-011</c>).
    /// </summary>
    public const string CookiePath = "/api/v1/auth";

    /// <summary>
    /// Issues the refresh cookie and a matching CSRF pair.
    /// </summary>
    /// <remarks>
    /// <paramref name="requireSecure"/> is false only in Development, where the API runs over plain
    /// HTTP; a <c>Secure</c> cookie would simply never be stored and every local login would appear
    /// to fail. Everywhere else HTTPS is mandatory (<c>SEC-008</c>).
    /// </remarks>
    public void IssueAuthCookies(HttpResponse response, string refreshToken, DateTimeOffset expiresAt, bool requireSecure)
    {
        response.Cookies.Append(RefreshTokenCookieName, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = requireSecure,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            Expires = expiresAt,
            IsEssential = true,
        });

        // Double-submit: the value is readable by script precisely so the client can copy it into
        // X-CSRF-Token. Its security comes from the same-origin policy — a cross-site page can cause
        // the cookie to be sent but cannot read it to build the matching header (AUTH-012).
        response.Cookies.Append(CsrfCookieName, GenerateCsrfToken(), new CookieOptions
        {
            HttpOnly = false,
            Secure = requireSecure,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            Expires = expiresAt,
            IsEssential = true,
        });
    }

    public void ClearAuthCookies(HttpResponse response, bool requireSecure)
    {
        // Path and SameSite must match the originals exactly, or the browser treats these as different
        // cookies and the old ones survive the logout.
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = requireSecure,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
        };

        response.Cookies.Delete(RefreshTokenCookieName, options);
        response.Cookies.Delete(CsrfCookieName, new CookieOptions
        {
            HttpOnly = false,
            Secure = requireSecure,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
        });
    }

    private static string GenerateCsrfToken() =>
        System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
