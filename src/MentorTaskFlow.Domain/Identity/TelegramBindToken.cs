using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Identity;

/// <summary>
/// A single-use token that ties a Telegram chat to an account (TZ 10.11, 19.2).
/// </summary>
/// <remarks>
/// <para>
/// Only the SHA-256 hash is stored, and comparison is constant-time (<c>TG-012</c>). The plain value
/// exists in one response and in one <c>/start</c> command, and is never written to a log, an audit
/// record, a metric or an error message (<c>TG-013</c>).
/// </para>
/// <para>
/// The binding uses the <see cref="UserId"/> of this row rather than anything Telegram sends
/// (<c>TG-009</c>). That is what makes another person's account impossible to claim without holding
/// their token: the chat identifies who is asking, never whom to bind.
/// </para>
/// </remarks>
public sealed class TelegramBindToken : BaseEntity
{
    /// <summary>Fifteen minutes — long enough to open Telegram, short enough to be worthless if seen.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Set once. A second use is refused even inside the lifetime (<c>TG-007</c>).</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    private TelegramBindToken()
    {
    }

    public static TelegramBindToken Issue(Guid userId, string tokenHash, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "UserId обязателен.");
        }

        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "TokenHash обязателен.");
        }

        return new TelegramBindToken
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAt = now.Add(Lifetime),
            CreatedAt = now,
        };
    }

    /// <summary>Whether the token may still be redeemed.</summary>
    public bool IsRedeemable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;

    /// <summary>
    /// Marks the token spent.
    /// </summary>
    /// <remarks>
    /// The caller writes <c>User.TelegramChatId</c> in the same transaction (<c>TG-007</c>): a token
    /// consumed without a binding would leave the person unable to try again until it expired.
    /// </remarks>
    public void Redeem(DateTimeOffset now)
    {
        if (!IsRedeemable(now))
        {
            throw new DomainException(
                DomainErrorCodes.TelegramBindTokenInvalid,
                "Ссылка привязки недействительна или её срок истёк.");
        }

        UsedAt = now;
    }

    /// <summary>
    /// Retires the token without binding anything (<c>TG-006</c>).
    /// </summary>
    /// <remarks>
    /// Issuing a new token invalidates the previous one, so a link that leaked stops working the
    /// moment the person notices and asks for another.
    /// </remarks>
    public void Invalidate(DateTimeOffset now) => UsedAt ??= now;
}
