using System.Diagnostics.Metrics;

namespace MentorTaskFlow.Infrastructure.Observability;

/// <summary>
/// The two request-level metrics of <c>OBS-007</c>.
/// </summary>
/// <remarks>
/// <para>
/// Emitted by the application rather than taken from the framework's own <c>http.server.*</c>
/// instruments, for one reason that matters: the label. ASP.NET Core's metrics are fine, but the
/// series names and the label set of 30.2 are part of the contract the alerts of 30.3 are written
/// against, and renaming them at scrape time would put the contract in the collector's configuration
/// instead of in the code.
/// </para>
/// <para>
/// <c>route</c> is the <b>route template</b> — <c>/api/v1/assignments/{id}</c> — never the request
/// path. The path carries identifiers, so using it would create one time series per assignment and
/// breach <c>OBS-010</c> on both counts: unbounded cardinality, and identifiers exposed on an
/// endpoint that has no authentication.
/// </para>
/// </remarks>
public sealed class HttpMetrics
{
    public const string MeterName = "MentorTaskFlow.Http";

    /// <summary>Used when a request matched no endpoint, so no template exists.</summary>
    public const string UnmatchedRoute = "unmatched";

    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;

    public HttpMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _requests = meter.CreateCounter<long>(
            "http_requests_total",
            description: "HTTP requests by method, route template and status (OBS-007).");

        _duration = meter.CreateHistogram<double>(
            "http_request_duration_seconds",
            unit: "s",
            description: "HTTP request duration, the source of the p95 of PERF-002.");
    }

    public void Record(string method, string route, int statusCode, double seconds)
    {
        _requests.Add(
            1,
            new KeyValuePair<string, object?>(MetricLabels.Method, method),
            new(MetricLabels.Route, route),
            new(MetricLabels.Status, statusCode));

        // Only the route: PERF-002 is a target per endpoint, and adding method and status would split
        // each endpoint's histogram across buckets too thin to compute a p95 from.
        _duration.Record(seconds, new KeyValuePair<string, object?>(MetricLabels.Route, route));
    }
}
