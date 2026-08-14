using MentorTaskFlow.Application.Common.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MentorTaskFlow.Api.HealthChecks;

/// <summary>
/// Object storage is a critical dependency (<c>REL-008</c>, TZ 30).
/// </summary>
/// <remarks>
/// Tagged <c>ready</c> rather than <c>live</c>: with storage down the process is perfectly healthy and
/// should not be restarted, but it cannot accept work, so it must be taken out of rotation instead.
/// </remarks>
public sealed class StorageHealthCheck(IFileStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await storage.IsAvailableAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Файловое хранилище недоступно.");
}
