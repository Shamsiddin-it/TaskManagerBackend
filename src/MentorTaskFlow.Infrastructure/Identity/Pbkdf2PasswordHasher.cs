using System.Security.Cryptography;
using MentorTaskFlow.Application.Common.Security;

namespace MentorTaskFlow.Infrastructure.Identity;

/// <summary>
/// PBKDF2-HMAC-SHA256, 100 000 iterations (TZ 10.1).
/// </summary>
/// <remarks>
/// <para>
/// The stored format matches ASP.NET Core Identity's version-3 layout, so the hashes stay readable by
/// the framework's own hasher if this class is ever swapped out:
/// <c>0x01 | prf (4 bytes BE) | iterations (4 bytes BE) | saltLength (4 bytes BE) | salt | subkey</c>.
/// </para>
/// <para>
/// The iteration count is read back <b>from the stored hash</b> rather than from configuration, so
/// raising the parameter does not invalidate every existing password.
/// </para>
/// </remarks>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const byte FormatMarker = 0x01;
    private const int Pbkdf2Sha256 = 1;
    private const int IterationCount = 100_000;
    private const int SaltSize = 16;
    private const int SubkeySize = 32;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, IterationCount, HashAlgorithmName.SHA256, SubkeySize);

        var output = new byte[13 + salt.Length + subkey.Length];
        output[0] = FormatMarker;
        WriteBigEndian(output.AsSpan(1, 4), Pbkdf2Sha256);
        WriteBigEndian(output.AsSpan(5, 4), IterationCount);
        WriteBigEndian(output.AsSpan(9, 4), salt.Length);
        salt.CopyTo(output.AsSpan(13));
        subkey.CopyTo(output.AsSpan(13 + salt.Length));

        return Convert.ToBase64String(output);
    }

    public bool Verify(string password, string passwordHash)
    {
        if (password is null || string.IsNullOrEmpty(passwordHash))
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(passwordHash);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length < 14 || decoded[0] != FormatMarker)
        {
            return false;
        }

        var prf = ReadBigEndian(decoded.AsSpan(1, 4));
        var iterations = ReadBigEndian(decoded.AsSpan(5, 4));
        var saltLength = ReadBigEndian(decoded.AsSpan(9, 4));

        if (prf != Pbkdf2Sha256 || iterations <= 0 || saltLength < 8)
        {
            return false;
        }

        var subkeyLength = decoded.Length - 13 - saltLength;
        if (subkeyLength < 16)
        {
            return false;
        }

        var salt = decoded.AsSpan(13, saltLength).ToArray();
        var expected = decoded.AsSpan(13 + saltLength, subkeyLength).ToArray();
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, subkeyLength);

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void WriteBigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static int ReadBigEndian(ReadOnlySpan<byte> source) =>
        (source[0] << 24) | (source[1] << 16) | (source[2] << 8) | source[3];
}
