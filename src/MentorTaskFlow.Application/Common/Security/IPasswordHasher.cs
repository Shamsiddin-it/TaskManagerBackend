namespace MentorTaskFlow.Application.Common.Security;

/// <summary>
/// Hashes and verifies passwords (TZ 10.1).
/// </summary>
/// <remarks>
/// The algorithm is PBKDF2-HMAC-SHA256 with 100 000 iterations, matching what ASP.NET Core Identity's
/// <c>PasswordHasher</c> produces. Behind an interface because the iteration count and the algorithm
/// are expected to be raised over the lifetime of the system, and the call sites must not care.
/// </remarks>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>
    /// Verifies a candidate. Implementations must compare in constant time and must not shortcut on a
    /// malformed stored hash — an early return would leak, through timing, which accounts have a
    /// password at all.
    /// </summary>
    bool Verify(string password, string passwordHash);
}
