namespace MentorTaskFlow.Api.Middleware;

/// <summary>
/// Applies the security headers required on every response by <c>SEC-009</c>.
/// </summary>
/// <remarks>
/// The Content-Security-Policy set here is the strict API default (<c>default-src 'none'</c>): the
/// API returns JSON and must never be a document origin. The relaxed preview policy of
/// <c>SEC-019</c> belongs to the file-preview endpoints and is applied there, in Phase 10.
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

            return Task.CompletedTask;
        });

        await next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
