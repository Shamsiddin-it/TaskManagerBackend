using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MentorTaskFlow.Api.HealthChecks;

/// <summary>
/// Writes health-check responses.
/// </summary>
/// <remarks>
/// <c>OBS-006</c> splits the contract: the <b>status code</b> is public, the detailed <b>body</b> is
/// for Admin only. Until authorization exists (Phase 3), the body is limited to per-check names and
/// statuses and never includes exception text, connection strings or host names — those would
/// disclose infrastructure topology to an anonymous caller.
/// </remarks>
public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
