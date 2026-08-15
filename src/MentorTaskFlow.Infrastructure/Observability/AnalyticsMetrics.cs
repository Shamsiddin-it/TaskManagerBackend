using System.Diagnostics.Metrics;

namespace MentorTaskFlow.Infrastructure.Observability;

/// <summary>
/// Counters of the analytics module (TZ 21.1).
/// </summary>
/// <remarks>
/// <c>ANA-007</c> requires negative durations to be excluded from aggregates <b>and</b> counted. The
/// counter is what makes the exclusion visible: a report that quietly drops rows and a report that has
/// no such rows look identical otherwise, and only one of them means the data is sound.
/// </remarks>
public sealed class AnalyticsMetrics
{
    public const string MeterName = "MentorTaskFlow.Analytics";

    private readonly Counter<long> _negativeDuration;
    private readonly Counter<long> _sampleSizeRefused;

    public AnalyticsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _negativeDuration = meter.CreateCounter<long>(
            "analytics_negative_duration_total",
            description: "Durations computed as negative and excluded from the aggregates (ANA-007).");

        _sampleSizeRefused = meter.CreateCounter<long>(
            "analytics_sample_size_refused_total",
            description: "Anonymised team reports refused for having fewer than five mentors (ANA-012).");
    }

    public void NegativeDuration(long count) => _negativeDuration.Add(count);

    public void SampleSizeRefused() => _sampleSizeRefused.Add(1);
}
