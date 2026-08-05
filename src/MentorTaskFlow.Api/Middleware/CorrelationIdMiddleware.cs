using Serilog.Context;

namespace MentorTaskFlow.Api.Middleware;

/// <summary>
/// Assigns a correlation id to every request and echoes it in the <c>X-Correlation-Id</c> response
/// header (<c>API-007</c>).
/// </summary>
/// <remarks>
/// An inbound value is reused so a caller can stitch a chain across services; otherwise a new one is
/// generated. The same value becomes the ProblemDetails <c>traceId</c> (<c>API-023</c>) and, from
/// Phase 3 onward, the <c>CorrelationId</c> of TaskEvent, AuditLog and NotificationOutbox rows —
/// one identifier reconstructs the whole action (<c>AUD-006</c>).
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    private const int MaxInboundLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Items[HeaderName] = correlationId;
        context.TraceIdentifier = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var inbound))
        {
            return Guid.CreateVersion7().ToString();
        }

        var candidate = inbound.ToString();

        // An inbound header is untrusted input that ends up in logs. Reject anything that is not a
        // short, printable, single-line token so it cannot forge log entries or response headers.
        if (candidate.Length is 0 or > MaxInboundLength || candidate.Any(c => char.IsControl(c)))
        {
            return Guid.CreateVersion7().ToString();
        }

        return candidate;
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
