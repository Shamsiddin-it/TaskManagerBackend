using MentorTaskFlow.Api.Authentication;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// Authentication and the password lifecycle (Приложение D.1).
/// </summary>
/// <remarks>
/// Every response here sets <c>Cache-Control: no-store</c>, so a shared proxy or the browser's back
/// button cannot resurface an access token (<c>API-019</c>).
/// </remarks>
[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public sealed class AuthController(
    IAuthService authService,
    AuthCookieManager cookieManager,
    ICurrentUserAccessor currentUser,
    IWebHostEnvironment environment) : ControllerBase
{
    /// <summary>
    /// <c>POST /auth/login</c> — anonymous, 10 requests per minute per IP (<c>SEC-007</c>).
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(request, RemoteIp, cancellationToken);
        return IssueSession(result);
    }

    /// <summary>
    /// <c>POST /auth/refresh</c> — authenticated by the <c>mtf_rt</c> cookie, guarded by CSRF.
    /// </summary>
    /// <remarks>
    /// The refresh token is read from the cookie and never from the body: a body parameter would be
    /// readable by page script, defeating the point of an HttpOnly cookie (<c>AUTH-010</c>).
    /// </remarks>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [RequireCsrf]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<LoginResponse>> RefreshAsync(CancellationToken cancellationToken)
    {
        var refreshToken = Request.Cookies[AuthCookieManager.RefreshTokenCookieName] ?? string.Empty;
        var result = await authService.RefreshAsync(refreshToken, RemoteIp, cancellationToken);
        return IssueSession(result);
    }

    /// <summary><c>POST /auth/logout</c> — revokes the token and clears both cookies.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [RequireCsrf]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
    {
        var refreshToken = Request.Cookies[AuthCookieManager.RefreshTokenCookieName] ?? string.Empty;

        await authService.LogoutAsync(refreshToken, RemoteIp, cancellationToken);

        cookieManager.ClearAuthCookies(Response, RequireSecureCookies);
        NoStore();

        return NoContent();
    }

    /// <summary>
    /// <c>GET /auth/me</c> — the authoritative profile, identical in shape to the login response
    /// (<c>AUTH-037</c>).
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<AuthUserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthUserDto>> MeAsync(CancellationToken cancellationToken)
    {
        var user = currentUser.Current
            ?? throw new Application.Common.Exceptions.UnauthorizedException();

        NoStore();
        return Ok(await authService.GetProfileAsync(user.UserId, cancellationToken));
    }

    /// <summary><c>POST /auth/change-password</c> — requires the current password (<c>AUTH-014</c>).</summary>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<LoginResponse>> ChangePasswordAsync(
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var user = currentUser.Current
            ?? throw new Application.Common.Exceptions.UnauthorizedException();

        var result = await authService.ChangePasswordAsync(user.UserId, request, RemoteIp, cancellationToken);
        return IssueSession(result);
    }

    /// <summary>
    /// <c>POST /auth/forgot-password</c> — always 202 with an identical body (<c>AUTH-015</c>).
    /// </summary>
    /// <remarks>
    /// The generated link is never returned to the caller. Doing so would let anyone reset any
    /// account by simply asking; delivery is by email, to the address that already owns the account.
    /// Until the notification module lands in Phase 3, the link is written to the log at Information.
    /// </remarks>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.ForgotPassword)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        [FromServices] ILogger<AuthController> logger,
        CancellationToken cancellationToken)
    {
        var link = await authService.ForgotPasswordAsync(request, RemoteIp, cancellationToken);

        if (link is not null)
        {
            logger.LogInformation("Password reset link issued: {ResetLink}", link);
        }

        NoStore();
        return Accepted();
    }

    /// <summary><c>POST /auth/reset-password</c> — TTL 30 minutes, single use (<c>AUTH-016</c>).</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PasswordToken)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await authService.ResetPasswordAsync(request, RemoteIp, cancellationToken);
        NoStore();
        return Ok();
    }

    /// <summary><c>POST /auth/set-password</c> — the invitation flow, TTL 24 hours (<c>AUTH-020</c>).</summary>
    [HttpPost("set-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PasswordToken)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetPasswordAsync(SetPasswordRequest request, CancellationToken cancellationToken)
    {
        await authService.SetPasswordAsync(request, RemoteIp, cancellationToken);
        NoStore();
        return Ok();
    }

    private ActionResult<LoginResponse> IssueSession(AuthenticationResult result)
    {
        cookieManager.IssueAuthCookies(
            Response,
            result.RefreshToken,
            result.RefreshTokenExpiresAt,
            RequireSecureCookies);

        NoStore();
        return Ok(result.Response);
    }

    /// <summary>Secure cookies everywhere but Development, where the API is served over plain HTTP.</summary>
    private bool RequireSecureCookies => !environment.IsDevelopment();

    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}
