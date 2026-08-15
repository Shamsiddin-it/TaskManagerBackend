using System.ComponentModel.DataAnnotations;

namespace MentorTaskFlow.Infrastructure.Options;

/// <summary>
/// The AI provider and the limits of Приложение L (TZ 22.2, 22.4).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> is the feature flag of 4.1. With it off the analytics endpoints are untouched
/// and only the summary endpoint answers 404: an installation that has not bought an AI subscription
/// must lose the summary block and nothing else (<c>AI-018</c>, <c>TEST-AI-002</c>).
/// </para>
/// <para>
/// The flag lives in this section rather than under <c>Features:</c> as 4.1 names it, for the same
/// reason it does for Telegram: the switch and the settings it governs belong together, or a
/// deployment ends up with one enabled and the other unconfigured.
/// </para>
/// <para>
/// Every volume limit has a safe default. They are not tuning knobs but the minimisation mechanism of
/// <c>AI-007</c> — a deployment that forgets to set them still sends at most fifty comments and at
/// most twenty thousand characters.
/// </para>
/// </remarks>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public bool Enabled { get; init; }

    /// <summary>From the environment only, like every other secret (<c>SEC-010</c>).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Named by <c>AI-002</c>; recorded on every report so the result is reproducible.</summary>
    [Required]
    public string ModelId { get; init; } = "claude-sonnet-5";

    /// <summary>Bumped whenever the prompt changes, which invalidates every cached report.</summary>
    [Required]
    [StringLength(16, MinimumLength = 1)]
    public string PromptVersion { get; init; } = "v1.0";

    [Range(1_000, 200_000)]
    public int MaxInputTokens { get; init; } = 12_000;

    [Range(256, 8_000)]
    public int MaxOutputTokens { get; init; } = 1_500;

    /// <summary>One attempt (<c>AI-002</c>).</summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>All attempts together; on exhaustion the answer is 503 (<c>AI-003</c>).</summary>
    [Range(1, 300)]
    public int TotalBudgetSeconds { get; init; } = 90;

    [Range(0, 5)]
    public int MaxRetries { get; init; } = 2;

    [Range(1, 200)]
    public int MaxComments { get; init; } = 50;

    [Range(50, 4_000)]
    public int MaxCommentChars { get; init; } = 500;

    [Range(1_000, 100_000)]
    public int MaxTotalChars { get; init; } = 20_000;

    /// <summary>Forced regenerations per subject per day (<c>AI-011</c>).</summary>
    [Range(1, 100)]
    public int ForceRegenerationPerDay { get; init; } = 1;

    /// <summary>
    /// The delays between attempts, in seconds (<c>AI-003</c>).
    /// </summary>
    /// <remarks>
    /// Two seconds then six. Deliberately short: the whole request is capped at ninety seconds and a
    /// person is waiting for it, so a long backoff would spend the budget on waiting rather than on
    /// attempts.
    /// </remarks>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(6),
    ];

    /// <summary>Whether the provider can actually be called.</summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}
