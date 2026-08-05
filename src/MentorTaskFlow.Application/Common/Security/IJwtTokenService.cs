using MentorTaskFlow.Domain.Users;

namespace MentorTaskFlow.Application.Common.Security;

/// <summary>An issued access token and the moment it stops being accepted.</summary>
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>
/// Issues access tokens (TZ 16.1).
/// </summary>
/// <remarks>
/// HS256, 15-minute TTL, signing key from the secret manager. The claim set is decided strictly by
/// role and administrative contour: a claim that does not apply is <b>omitted</b>, never written as
/// null (<c>AUTH-031</c>).
/// </remarks>
public interface IJwtTokenService
{
    AccessToken Issue(User user);
}

/// <summary>
/// Answers whether a presented <c>tv</c> claim is still current (<c>AUTH-028</c>).
/// </summary>
/// <remarks>
/// <para>
/// A signed token cannot be recalled, so revocation works by versioning: every change of scope or
/// access level increments <c>User.TokenVersion</c>, and tokens carrying the old value stop being
/// accepted. Version 2.0 claimed a <c>SecurityStamp</c> claim «invalidates current access tokens»,
/// which is simply untrue — a claim inside a signed token revokes nothing.
/// </para>
/// <para>
/// The current value is cached for 30 seconds, so the guarantee is stated honestly: revocation takes
/// effect <b>within 30 seconds</b> for access tokens and <b>immediately</b> for refresh tokens, which
/// is what makes extending a revoked session impossible.
/// </para>
/// </remarks>
public interface ITokenVersionValidator
{
    Task<TokenVersionCheck> CheckAsync(Guid userId, int presentedTokenVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the cached entry after a local change, so the instance that made it converges at once and
    /// the others within the cache TTL (<c>AUTH-029</c>).
    /// </summary>
    void Invalidate(Guid userId);
}

public enum TokenVersionCheck
{
    /// <summary>The token is current and the account is usable.</summary>
    Valid = 0,

    /// <summary>The user no longer exists, or the claim does not match the stored version.</summary>
    Mismatch = 1,

    /// <summary>The account is deactivated — reported separately so the caller can answer 401 <c>USER_DEACTIVATED</c>.</summary>
    Deactivated = 2,
}
