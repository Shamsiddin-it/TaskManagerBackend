namespace MentorTaskFlow.Application.Common.Security;

/// <summary>
/// Generates opaque bearer secrets — refresh tokens, password tokens, Telegram bind tokens — and
/// hashes them for storage.
/// </summary>
/// <remarks>
/// One service for all three because the rules are identical: 256 bits from a cryptographic RNG,
/// Base64Url on the wire, SHA-256 at rest, constant-time comparison. Separate implementations would
/// drift, and the weakest one would define the security of the whole system.
/// </remarks>
public interface ISecureTokenService
{
    /// <summary>
    /// Returns the value handed to the user and the hash to persist.
    /// </summary>
    /// <remarks>
    /// Both at once, deliberately: it makes «store the plain value by accident» hard to write, since
    /// the caller must actively pick the wrong half of the tuple.
    /// </remarks>
    (string PlainToken, string TokenHash) Generate();

    /// <summary>Hashes a presented token so it can be matched against stored rows.</summary>
    string HashToken(string plainToken);

    /// <summary>
    /// Constant-time comparison of two hashes (<c>TG-012</c>). Ordinary string equality returns as
    /// soon as it finds a differing byte, which turns the comparison into an oracle.
    /// </summary>
    bool FixedTimeEquals(string hashA, string hashB);
}
