namespace MentorTaskFlow.Contracts.Analytics;

/// <summary>What the caller is asking a summary about (<c>AI-020</c>).</summary>
/// <remarks>
/// Named rather than inferred. A request whose scope is guessed from which optional ids happen to be
/// present is a request whose authorization is guessed too, and <c>TEN-077</c> requires the branch
/// check to happen before the cache is consulted at all.
/// </remarks>
public enum AiSummaryScopeDto
{
    Personal = 0,
    Team = 1,
    Branch = 2,
    Organization = 3,
}

/// <summary>
/// <c>POST /reports/ai-summary</c> (Приложение D.6).
/// </summary>
/// <remarks>
/// The period follows the same rule as the metric endpoints: local dates of the reporting zone, both
/// inclusive (<c>ANA-002</c>). Omitted, it is the last thirty days.
/// </remarks>
public sealed record AiSummaryRequest
{
    public required AiSummaryScopeDto Scope { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    /// <summary>Organization Admin only. A Branch Admin naming another branch gets 404 (<c>TEN-077</c>).</summary>
    public Guid? BranchId { get; init; }

    public Guid? CategoryId { get; init; }

    /// <summary>The subject of a personal report. A Mentor may name only themselves.</summary>
    public Guid? MentorId { get; init; }

    public bool IncludeCancelled { get; init; }

    /// <summary>Regenerate rather than serve the cached report — once a day per subject (<c>AI-011</c>).</summary>
    public bool Force { get; init; }
}

/// <summary>
/// A generated summary and everything needed to judge it.
/// </summary>
/// <remarks>
/// <para>
/// <c>metrics</c> is returned alongside the text on purpose: <c>AI-022</c> requires the reader to be
/// told the figures were computed by the system and not by the model, and the cheapest way to make
/// that true rather than merely stated is to hand over both.
/// </para>
/// <para>
/// <c>content</c> is plain text with Markdown permitted and HTML not (<c>AI-017</c>); it is model
/// output and must be escaped wherever it is displayed.
/// </para>
/// </remarks>
public sealed record AiSummaryDto(
    Guid Id,
    AiSummaryScopeDto Scope,
    DateOnly From,
    DateOnly To,
    string PeriodTimeZoneId,
    bool IsPartialPeriod,
    string ModelId,
    string PromptVersion,
    bool FromCache,
    DateTimeOffset GeneratedAt,
    string Content,
    MetricsDto Metrics,
    string Disclaimer)
{
    /// <summary>The wording of <c>AI-022</c>, carried by the response so no client can forget it.</summary>
    public const string StandardDisclaimer =
        "Резюме создано с помощью ИИ и может содержать неточности. " +
        "Основные метрики рассчитаны системой независимо от ИИ.";
}
