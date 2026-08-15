using MentorTaskFlow.Contracts.Telegram;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>Account binding for the Telegram channel (TZ 19.2).</summary>
public interface ITelegramService
{
    /// <summary>
    /// Issues a single-use bind token and its deep link (<c>TG-005</c>).
    /// </summary>
    /// <remarks>
    /// The previous live token of the same user is retired (<c>TG-006</c>), so a link that leaked stops
    /// working the moment its owner notices and asks for another.
    /// </remarks>
    Task<TelegramBindTokenDto> IssueBindTokenAsync(CancellationToken cancellationToken);

    Task<TelegramStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Removes the binding (<c>TG-010</c>).
    /// </summary>
    /// <remarks>
    /// Afterwards <c>TelegramPreferred</c> events go by email automatically, so unbinding silences
    /// nothing (<c>NTF-002</c>).
    /// </remarks>
    Task UnbindAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Redeems a token presented as <c>/start &lt;token&gt;</c> and returns the reply for the chat.
    /// </summary>
    /// <remarks>
    /// The account bound is the one that issued the token, taken from the stored row and never from
    /// anything Telegram sent: the chat says who is asking, not whom to bind (<c>TG-009</c>).
    /// </remarks>
    Task<string> RedeemBindTokenAsync(string chatId, string? plainToken, CancellationToken cancellationToken);
}
