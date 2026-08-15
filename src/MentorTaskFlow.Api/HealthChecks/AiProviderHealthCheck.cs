using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Infrastructure.Observability;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MentorTaskFlow.Api.HealthChecks;

/// <summary>
/// The AI provider as an <b>optional</b> readiness dependency (<c>AI-019</c>, TZ 30.1).
/// </summary>
/// <remarks>
/// <para>
/// It never reports <see cref="HealthStatus.Unhealthy"/>, whatever the provider is doing. Everything
/// the product exists to do — assignments, submissions, reviews, the metrics of section 21 — works
/// without it, so taking the instance out of rotation because a summarisation API is rate-limiting
/// would turn a missing paragraph of prose into an outage (<c>AI-018</c>).
/// </para>
/// <para>
/// No request is made from here. A probe that called the provider would bill the organization for
/// every poll and would answer «not ready» precisely when the provider is throttling — neither of
/// which is a fact about this process. The check reports the last outcome the provider observed
/// instead.
/// </para>
/// </remarks>
public sealed class AiProviderHealthCheck(
    IAiSummaryProvider provider,
    AiProviderStatus status,
    IClock clock) : IHealthCheck
{
    /// <summary>How long a failure keeps the dependency degraded before it is assumed transient.</summary>
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!provider.IsConfigured)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "AI-провайдер не настроен: отчёты доступны без блока резюме."));
        }

        return Task.FromResult(status.HasRecentFailure(clock.UtcNow, FailureWindow)
            ? HealthCheckResult.Degraded("Последний вызов AI-провайдера завершился ошибкой.")
            : HealthCheckResult.Healthy());
    }
}
