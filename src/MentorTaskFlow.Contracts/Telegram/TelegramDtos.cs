namespace MentorTaskFlow.Contracts.Telegram;

/// <summary>
/// <c>POST /telegram/bind-token</c> (<c>TG-005</c>).
/// </summary>
/// <remarks>
/// The plain token is returned exactly once, here, and is never written to a log, an audit record, a
/// metric or an error message (<c>TG-013</c>). The deep link is assembled server-side so the client
/// cannot be pointed at another bot by a crafted response.
/// </remarks>
public sealed record TelegramBindTokenDto(string Token, string DeepLink, DateTimeOffset ExpiresAt);

/// <summary>
/// <c>GET /telegram/status</c>.
/// </summary>
/// <remarks>
/// The chat identifier is not returned. Whether a binding exists is what the interface needs; the
/// identifier itself is of no use to the account holder and would be one more thing to leak.
/// </remarks>
public sealed record TelegramStatusDto(bool IsBound, DateTimeOffset? BoundAt);
