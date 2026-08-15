using System.Threading.RateLimiting;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using Microsoft.AspNetCore.RateLimiting;

namespace MentorTaskFlow.Api.Authentication;

/// <summary>
/// Rate-limit policy names and their registration. Values: Приложение L.2 (<c>SEC-007</c>).
/// </summary>
/// <remarks>
/// Fixed window, keyed by IP for anonymous endpoints and by user id for authenticated ones. The
/// anonymous limits exist because those endpoints are reachable without credentials and are the
/// natural targets for credential stuffing and account enumeration.
/// </remarks>
public static class RateLimitPolicies
{
    /// <summary><c>POST /auth/login</c> — 10 per minute per IP.</summary>
    public const string Login = "auth-login";

    /// <summary><c>POST /auth/forgot-password</c> — 5 per hour per IP.</summary>
    public const string ForgotPassword = "auth-forgot-password";

    /// <summary><c>reset-password</c> and <c>set-password</c> — 10 per hour per IP.</summary>
    public const string PasswordToken = "auth-password-token";

    /// <summary>Everything else that is authenticated — 300 per minute per user.</summary>
    public const string Default = "authenticated-default";

    /// <summary>
    /// <c>POST /telegram/webhook</c> — 60 per minute per IP (<c>TG-003</c>).
    /// </summary>
    /// <remarks>
    /// Anonymous by necessity: Telegram cannot present a credential. The shared secret authenticates
    /// the caller, and this limit bounds what an unauthenticated prober can cost us before the secret
    /// check rejects them.
    /// </remarks>
    public const string TelegramWebhook = "telegram-webhook";

    public static IServiceCollection AddMentorTaskFlowRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(Login, context => FixedWindowByIp(context, permitLimit: 10, TimeSpan.FromMinutes(1)));
            limiter.AddPolicy(ForgotPassword, context => FixedWindowByIp(context, permitLimit: 5, TimeSpan.FromHours(1)));
            limiter.AddPolicy(PasswordToken, context => FixedWindowByIp(context, permitLimit: 10, TimeSpan.FromHours(1)));
            limiter.AddPolicy(TelegramWebhook, context => FixedWindowByIp(context, permitLimit: 60, TimeSpan.FromMinutes(1)));
            limiter.AddPolicy(Default, FixedWindowByUser);

            limiter.OnRejected = (context, _) =>
            {
                // Retry-After lets a well-behaved client back off instead of hammering (SEC-007).
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();
                }

                // Thrown, not written: the exception middleware turns it into the same ProblemDetails
                // shape as every other failure, so 429 is not a special case for the client (TZ 26.1).
                throw new TooManyRequestsException();
            };
        });

    private static RateLimitPartition<string> FixedWindowByIp(HttpContext context, int permitLimit, TimeSpan window)
    {
        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Partitioned by route as well as by IP, per SEC-007's "IP + маршрут" key: exhausting the
        // login budget must not also block a password reset from the same office NAT.
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{context.Request.Path}|{key}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
            });
    }

    private static RateLimitPartition<string> FixedWindowByUser(HttpContext context)
    {
        // Falls back to the address for anonymous callers, so the limiter still partitions rather
        // than lumping every unauthenticated request into one bucket.
        var key = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst("sub")?.Value ?? "authenticated"
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    }
}

// TooManyRequestsException moved to MentorTaskFlow.Application in Phase 13: services enforce
// per-actor limits the transport-level limiter cannot see (TG-014).
