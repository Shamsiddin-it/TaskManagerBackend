using MentorTaskFlow.Api.Options;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Security;
using MentorTaskFlow.Contracts.Common;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Api.Authentication;

/// <summary>
/// Enforces both CSRF defences of <c>AUTH-012</c> on the two cookie-authenticated endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>POST /auth/refresh</c> and <c>POST /auth/logout</c> authenticate by cookie; every other
/// endpoint uses a Bearer token and is therefore not exposed to CSRF at all (<c>AUTH-011</c>). Applying
/// this filter more widely would add cost without adding protection.
/// </para>
/// <para>
/// Both checks are required, not either-or. <c>Origin</c> is absent on some legitimate requests and
/// spoofable outside a browser, while the double-submit token is unreadable cross-site but travels in
/// a cookie that a confused-deputy request would still send. Together they cover each other's gap.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireCsrfAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var httpContext = context.HttpContext;
        var corsOptions = httpContext.RequestServices.GetRequiredService<IOptions<CorsOptions>>().Value;
        var tokens = httpContext.RequestServices.GetRequiredService<ISecureTokenService>();

        ValidateOrigin(httpContext, corsOptions);
        ValidateDoubleSubmitToken(httpContext, tokens);

        await next();
    }

    private static void ValidateOrigin(HttpContext context, CorsOptions corsOptions)
    {
        // Origin first, Referer as the fallback: some browsers omit Origin on same-origin requests,
        // and rejecting those would break the refresh flow for real users.
        var origin = context.Request.Headers.Origin.FirstOrDefault();

        if (string.IsNullOrEmpty(origin))
        {
            var referer = context.Request.Headers.Referer.FirstOrDefault();

            if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            {
                origin = refererUri.GetLeftPart(UriPartial.Authority);
            }
        }

        // Neither header present: not a browser-initiated cross-site request, so the double-submit
        // token below carries the check on its own.
        if (string.IsNullOrEmpty(origin))
        {
            return;
        }

        var allowed = corsOptions.AllowedOrigins.Any(candidate =>
            string.Equals(candidate.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            throw new ForbiddenException(ErrorCodes.Forbidden, "Источник запроса не разрешён.");
        }
    }

    private static void ValidateDoubleSubmitToken(HttpContext context, ISecureTokenService tokens)
    {
        var cookieToken = context.Request.Cookies[AuthCookieManager.CsrfCookieName];
        var headerToken = context.Request.Headers[AuthCookieManager.CsrfHeaderName].FirstOrDefault();

        if (string.IsNullOrEmpty(cookieToken) || string.IsNullOrEmpty(headerToken))
        {
            throw new ForbiddenException(ErrorCodes.CsrfValidationFailed, "CSRF-токен отсутствует.");
        }

        // Constant-time: an ordinary comparison returns on the first differing byte, which would let a
        // caller recover the token one character at a time from response timing.
        if (!tokens.FixedTimeEquals(cookieToken, headerToken))
        {
            throw new ForbiddenException(ErrorCodes.CsrfValidationFailed, "CSRF-токен не совпадает.");
        }
    }
}
