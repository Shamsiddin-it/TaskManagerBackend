using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Identity;

/// <summary>What a <see cref="UserSecurityToken"/> is for (TZ 10.12).</summary>
public enum SecurityTokenPurpose
{
    /// <summary>First password after an invitation. TTL 24 hours (<c>AUTH-020</c>).</summary>
    SetPassword = 0,

    /// <summary>Password reset. TTL 30 minutes (<c>AUTH-016</c>).</summary>
    ResetPassword = 1,
}

/// <summary>
/// A single-use token for setting or resetting a password (TZ 10.12).
/// </summary>
/// <remarks>
/// <para>
/// Introduced in 2.1 to replace the ambiguity «Identity DataProtector or a hash in the database».
/// <c>DataProtector</c> was rejected outright: it keeps no server-side state, so an issued token
/// cannot be revoked — and revocation is exactly what <c>AUTH-017</c> requires (<c>AUTH-016</c>).
/// </para>
/// <para>
/// Generated passwords are never created, mailed or displayed anywhere, including for the very first
/// administrator (<c>AUTH-019</c>). This token is the only path to a password.
/// </para>
/// </remarks>
public sealed class UserSecurityToken : BaseEntity
{
    public Guid UserId { get; private set; }

    public SecurityTokenPurpose Purpose { get; private set; }

    /// <summary>SHA-256 of a 256-bit random value; the plain token is never stored.</summary>
    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Set once. A second redemption is refused (<c>AUTH-030</c>).</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>Set when a newer token of the same purpose is issued (<c>AUTH-017</c>).</summary>
    public DateTimeOffset? InvalidatedAt { get; private set; }

    public string? CreatedByIp { get; private set; }

    /// <summary>
    /// Active means unused, not invalidated and not expired (<c>AUTH-030</c>). At most one token per
    /// purpose per user may be active at a time, guaranteed by the partial unique index
    /// <c>ux_user_security_tokens_active</c> rather than by service code.
    /// </summary>
    public bool IsActive(DateTimeOffset now) => UsedAt is null && InvalidatedAt is null && ExpiresAt > now;

    private UserSecurityToken()
    {
    }

    public static UserSecurityToken Issue(
        Guid userId,
        SecurityTokenPurpose purpose,
        string tokenHash,
        TimeSpan lifetime,
        string? createdByIp,
        DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "UserId обязателен.");
        }

        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "TokenHash обязателен.");
        }

        return new UserSecurityToken
        {
            UserId = userId,
            Purpose = purpose,
            TokenHash = tokenHash,
            ExpiresAt = now.Add(lifetime),
            CreatedByIp = createdByIp,
            CreatedAt = now,
        };
    }

    /// <summary>Consumes the token. Returns false when it was already spent, expired or superseded.</summary>
    public bool TryRedeem(DateTimeOffset now)
    {
        if (!IsActive(now))
        {
            return false;
        }

        UsedAt = now;
        return true;
    }

    /// <summary>
    /// Retires this token because a newer one of the same purpose was issued, enforcing the
    /// «exactly one live link» rule (<c>AUTH-017</c>).
    /// </summary>
    public void Invalidate(DateTimeOffset now)
    {
        if (UsedAt is not null || InvalidatedAt is not null)
        {
            return;
        }

        InvalidatedAt = now;
    }

    public void ForgetIpAddress() => CreatedByIp = null;
}
