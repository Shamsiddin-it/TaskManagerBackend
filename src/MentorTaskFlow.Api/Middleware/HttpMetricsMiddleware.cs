using System.Diagnostics;
using MentorTaskFlow.Infrastructure.Observability;
using Microsoft.AspNetCore.Routing.Patterns;

namespace MentorTaskFlow.Api.Middleware;

/// <summary>
/// Records <c>http_requests_total</c> and <c>http_request_duration_seconds</c> (<c>OBS-007</c>).
/// </summary>
/// <remarks>
/// <para>
/// Placed after routing so the route <b>template</b> is known. That ordering is the whole point: the
/// template is a bounded set fixed by the code, while the path contains identifiers and would create
/// a time series per assignment — unbounded cardinality, and identifiers on an endpoint with no
/// authentication (<c>OBS-010</c>).
/// </para>
/// <para>
/// The duration is measured around the rest of the pipeline, so it includes handler, database and
/// serialisation — which is what <c>PERF-002</c> means by the p95 of an endpoint. It is <b>not</b>
/// the client's latency: the network is outside this process and outside the target.
/// </para>
/// </remarks>
public sealed class HttpMetricsMiddleware(RequestDelegate next, HttpMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var timestamp = Stopwatch.GetTimestamp();

        try
        {
            await next(context);
        }
        finally
        {
            metrics.Record(
                context.Request.Method,
                RouteOf(context),
                context.Response.StatusCode,
                Stopwatch.GetElapsedTime(timestamp).TotalSeconds);
        }
    }

    /// <summary>
    /// The matched route template, or a single bucket for everything unmatched.
    /// </summary>
    /// <remarks>
    /// Unmatched requests share one label rather than carrying their paths. A scan for
    /// <c>/wp-login.php</c> would otherwise add a series per probed URL, which is an unauthenticated
    /// way to fill the collector's memory.
    /// </remarks>
    private static string RouteOf(HttpContext context) =>
        context.GetEndpoint() is RouteEndpoint { RoutePattern: { } pattern }
            ? RawTextOf(pattern)
            : HttpMetrics.UnmatchedRoute;

    private static string RawTextOf(RoutePattern pattern) =>
        pattern.RawText is { Length: > 0 } text ? $"/{text.TrimStart('/')}" : HttpMetrics.UnmatchedRoute;
}
