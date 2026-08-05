using System.Reflection;
using MentorTaskFlow.Application.Common.Security;

namespace MentorTaskFlow.Infrastructure.Identity;

/// <summary>
/// The common-password deny list of <c>AUTH-013</c>, loaded from an embedded resource.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shipped list is a seed, not the 10 000 entries the TZ asks for.</b> Vendoring a corpus such
/// as SecLists <c>rockyou-10000</c> is a decision about pulling third-party data into the repository,
/// so it is left for the customer to approve rather than taken unilaterally. Everything around the
/// list is finished: swapping the resource file for the full corpus needs no code change.
/// </para>
/// <para>
/// Loaded once into a case-insensitive set. A file read per password check would put disk I/O on the
/// login path, and the list is small enough that memory is not a concern.
/// </para>
/// </remarks>
public sealed class EmbeddedCommonPasswordCatalog : ICommonPasswordCatalog
{
    private const string ResourceName = "MentorTaskFlow.Infrastructure.Identity.common-passwords.txt";

    private readonly HashSet<string> _passwords;

    public EmbeddedCommonPasswordCatalog()
    {
        _passwords = Load();
    }

    public int Count => _passwords.Count;

    public bool Contains(string password) =>
        !string.IsNullOrEmpty(password) && _passwords.Contains(password);

    private static HashSet<string> Load()
    {
        // OrdinalIgnoreCase: «PASSWORD1234» must be refused just as «Password1234» is. Ordinal rather
        // than a culture-aware comparer, so the deny list behaves identically on every host.
        var passwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' is missing. AUTH-013 requires a common-password list.");

        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            passwords.Add(trimmed);
        }

        return passwords;
    }
}
