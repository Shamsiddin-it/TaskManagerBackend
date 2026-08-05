using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Identity;

/// <summary>
/// A stored refresh token, participating in a rotation chain (TZ 10.10).
/// </summary>
/// <remarks>
/// <para>
/// Only the SHA-256 hash is persisted. The plain value is never stored, never logged and never
/// written to the AuditLog (<c>AUTH-006</c>, <c>SEC-021</c>): a database dump must not be enough to
/// resume somebody's session.
/// </para>
/// <para>
/// Tokens issued from one another share a <see cref="FamilyId"/>. Presenting a token that was already
/// rotated means the value leaked, so the entire family is revoked at once rather than just the
/// replayed token — the attacker and the legitimate holder are indistinguishable at that point, and
/// ending both sessions is the only safe answer (<c>AUTH-008</c>).
/// </para>
/// </remarks>
public sealed class RefreshToken : BaseEntity
{
    public Guid UserId { get; private set; }

    /// <summary>SHA-256 of the token, lowercase hex, <c>char(64)</c>.</summary>
    public string TokenHash { get; private set; } = null!;

    /// <summary>Identifies the rotation chain this token belongs to.</summary>
    public Guid FamilyId { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Zeroed by retention after 90 days — it is personal data (TZ 27.5).</summary>
    public string? CreatedByIp { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedByIp { get; private set; }

    /// <summary>Set on rotation, so the chain can be walked during an investigation.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    public RefreshTokenRevocationReason? ReasonRevoked { get; private set; }

    /// <summary>
    /// Usable means: not revoked and not expired. Note that a token can be <b>revoked but still
    /// presentable</b> — that is precisely the reuse case, so callers must look up by hash first and
    /// check this second, never filter revoked rows out of the query.
    /// </summary>
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    private RefreshToken()
    {
    }

    /// <summary>Starts a new chain — issued at login.</summary>
    public static RefreshToken IssueNewFamily(Guid userId, string tokenHash, TimeSpan lifetime, string? createdByIp, DateTimeOffset now) =>
        Issue(userId, tokenHash, Guid.CreateVersion7(), lifetime, createdByIp, now);

    /// <summary>Continues an existing chain — issued on rotation.</summary>
    public static RefreshToken IssueInFamily(Guid userId, string tokenHash, Guid familyId, TimeSpan lifetime, string? createdByIp, DateTimeOffset now) =>
        Issue(userId, tokenHash, familyId, lifetime, createdByIp, now);

    private static RefreshToken Issue(
        Guid userId,
        string tokenHash,
        Guid familyId,
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

        return new RefreshToken
        {
            UserId = userId,
            TokenHash = tokenHash,
            FamilyId = familyId,
            ExpiresAt = now.Add(lifetime),
            CreatedByIp = createdByIp,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Marks this token as replaced by <paramref name="replacementId"/>.
    /// </summary>
    /// <remarks>
    /// Revoking is idempotent by design: a token already revoked keeps its original reason. Overwriting
    /// <see cref="ReasonRevoked"/> would let a later rotation mask an earlier <c>ReuseDetected</c>,
    /// erasing the evidence of the very event the field exists to record.
    /// </remarks>
    public void Rotate(Guid replacementId, string? revokedByIp, DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedByIp = revokedByIp;
        ReplacedByTokenId = replacementId;
        ReasonRevoked = RefreshTokenRevocationReason.Rotated;
    }

    public void Revoke(RefreshTokenRevocationReason reason, string? revokedByIp, DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedByIp = revokedByIp;
        ReasonRevoked = reason;
    }

    /// <summary>Retention: the address is dropped after 90 days, the row itself survives (TZ 27.5).</summary>
    public void ForgetIpAddresses()
    {
        CreatedByIp = null;
        RevokedByIp = null;
    }
}
