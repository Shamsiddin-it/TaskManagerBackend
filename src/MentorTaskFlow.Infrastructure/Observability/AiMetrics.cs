using System.Diagnostics.Metrics;

namespace MentorTaskFlow.Infrastructure.Observability;

/// <summary>
/// Cost and availability of the AI provider (<c>AI-021</c>).
/// </summary>
/// <remarks>
/// <c>ai_tokens_total</c> exists so a monthly budget can be alerted on rather than discovered on an
/// invoice. Tokens are the only unit the provider charges in, so counting requests instead would
/// track something that does not correlate with spend — one report over fifty comments costs many
/// times what one over none does.
/// </remarks>
public sealed class AiMetrics
{
    public const string MeterName = "MentorTaskFlow.Ai";

    private readonly Counter<long> _tokens;
    private readonly Counter<long> _generated;
    private readonly Counter<long> _cacheHits;
    private readonly Counter<long> _failures;

    public AiMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _tokens = meter.CreateCounter<long>(
            "ai_tokens_total",
            description: "Tokens billed by the AI provider, by direction (AI-021).");

        _generated = meter.CreateCounter<long>(
            "ai_summaries_generated_total",
            description: "Summaries that required a call to the provider.");

        _cacheHits = meter.CreateCounter<long>(
            "ai_summaries_cache_hits_total",
            description: "Summaries served from the cache without touching the provider (AI-010).");

        _failures = meter.CreateCounter<long>(
            "ai_provider_failures_total",
            description: "Provider calls that exhausted their retries or time budget (AI-003).");
    }

    public void Tokens(string modelId, int? input, int? output)
    {
        if (input is { } inputTokens)
        {
            _tokens.Add(inputTokens, new KeyValuePair<string, object?>("model", modelId), new("direction", "input"));
        }

        if (output is { } outputTokens)
        {
            _tokens.Add(outputTokens, new KeyValuePair<string, object?>("model", modelId), new("direction", "output"));
        }
    }

    public void Generated(string scope) => _generated.Add(1, new KeyValuePair<string, object?>("scope", scope));

    public void CacheHit(string scope) => _cacheHits.Add(1, new KeyValuePair<string, object?>("scope", scope));

    public void Failure(string reason) => _failures.Add(1, new KeyValuePair<string, object?>("reason", reason));
}

/// <summary>
/// The last thing the provider did, for the readiness probe.
/// </summary>
/// <remarks>
/// The health check reads this instead of calling the provider itself. A readiness probe that made a
/// real API call would bill the organization for every kubelet poll and would answer «not ready» at
/// precisely the moment the provider is rate-limiting — neither of which says anything about whether
/// this process can serve traffic (<c>AI-019</c>).
/// </remarks>
public sealed class AiProviderStatus
{
    private long _lastFailureTicks;

    public void RecordSuccess() => Interlocked.Exchange(ref _lastFailureTicks, 0);

    public void RecordFailure(DateTimeOffset at) =>
        Interlocked.Exchange(ref _lastFailureTicks, at.UtcTicks);

    /// <summary>Whether a failure was seen recently enough to be worth reporting.</summary>
    public bool HasRecentFailure(DateTimeOffset now, TimeSpan window)
    {
        var ticks = Interlocked.Read(ref _lastFailureTicks);

        return ticks != 0 && now.UtcTicks - ticks < window.Ticks;
    }
}
