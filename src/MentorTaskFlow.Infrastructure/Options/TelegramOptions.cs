using System.ComponentModel.DataAnnotations;

namespace MentorTaskFlow.Infrastructure.Options;

/// <summary>
/// The Telegram bot (TZ 19, Приложение L).
/// </summary>
/// <remarks>
/// <see cref="Enabled"/> is the feature flag of 4.1: with it off the bind endpoints answer 404 —
/// not 403 — because a capability the installation does not have should be indistinguishable from one
/// that does not exist.
/// </remarks>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public bool Enabled { get; init; }

    /// <summary>Used to build the deep link; never the token (<c>TG-005</c>).</summary>
    public string BotUsername { get; init; } = "mentortaskflow_bot";

    /// <summary>From the environment only, like every other secret (<c>SEC-010</c>).</summary>
    public string? BotToken { get; init; }

    /// <summary>
    /// Compared constant-time against <c>X-Telegram-Bot-Api-Secret-Token</c> (<c>TG-002</c>).
    /// </summary>
    /// <remarks>
    /// The webhook is anonymous to the outside world, so this header is the only thing separating
    /// Telegram from anyone who has guessed the URL.
    /// </remarks>
    public string? WebhookSecret { get; init; }

    /// <summary>Issuing a bind token: 5 per hour per user (<c>TG-014</c>, <c>SEC-007</c>).</summary>
    [Range(1, 100)]
    public int BindTokenRequestsPerHour { get; init; } = 5;

    /// <summary>Redeeming one: 20 attempts per hour per chat (<c>TG-014</c>).</summary>
    [Range(1, 1000)]
    public int BindAttemptsPerHourPerChat { get; init; } = 20;
}
