using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MentorTaskFlow.Application.Common.Security;

namespace MentorTaskFlow.Infrastructure.Identity;

/// <summary>
/// 256-bit random tokens, Base64Url on the wire, SHA-256 at rest.
/// </summary>
/// <remarks>
/// 256 bits of entropy makes brute force infeasible inside any of the lifetimes involved — 15 minutes
/// for a Telegram bind token, 30 minutes for a password reset (<c>TG-011</c>).
/// </remarks>
public sealed class SecureTokenService : ISecureTokenService
{
    private const int TokenBytes = 32;

    public (string PlainToken, string TokenHash) Generate()
    {
        // Base64Url, so the value survives a URL path or query without escaping — password links carry
        // it as `?token=…`. 32 bytes encode to 43 characters with no padding.
        var plainToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        return (plainToken, HashToken(plainToken));
    }

    /// <summary>
    /// Lowercase hex SHA-256, matching the <c>char(64)</c> columns of TZ 11.2.
    /// </summary>
    /// <remarks>
    /// A plain hash with no salt or work factor, and that is correct here: the input is 256 bits of
    /// uniform randomness, so there is no dictionary to defend against, and password-style stretching
    /// would only slow down every request that verifies a token.
    /// </remarks>
    public string HashToken(string plainToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(plainToken);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plainToken));
        return Convert.ToHexStringLower(hash);
    }

    public bool FixedTimeEquals(string hashA, string hashB)
    {
        if (hashA is null || hashB is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hashA),
            Encoding.UTF8.GetBytes(hashB));
    }
}
