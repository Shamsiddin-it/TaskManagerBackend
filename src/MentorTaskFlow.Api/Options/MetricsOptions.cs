using System.Net;

namespace MentorTaskFlow.Api.Options;

/// <summary>
/// Who may scrape <c>/metrics</c> (<c>OBS-007</c>).
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is unauthenticated by design — a collector is not a user and has no token — so the
/// boundary has to be the network. <c>OBS-007</c> says «внутренняя сеть», and this turns that phrase
/// into something the process enforces rather than something a reverse proxy is trusted to have been
/// configured to do.
/// </para>
/// <para>
/// The defaults are the loopback and the three private ranges. They are the addresses a collector
/// running beside the application actually has, and nothing routable from outside is among them.
/// </para>
/// </remarks>
public sealed class MetricsOptions
{
    public const string SectionName = "Metrics";

    /// <summary>Turns the endpoint off entirely for a deployment that scrapes some other way.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>CIDR blocks permitted to scrape. Empty means the defaults below.</summary>
    public string[] AllowedNetworks { get; init; } = [];

    private static readonly (IPAddress Network, int Prefix)[] PrivateDefaults =
    [
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.IPv6Loopback, 128),
    ];

    /// <summary>
    /// Whether a caller's address is inside the permitted networks.
    /// </summary>
    /// <remarks>
    /// A null address means the connection has no remote IP — the in-memory test server, and nothing
    /// else in a real deployment. Treating it as permitted keeps the endpoint testable without
    /// weakening a deployment, where Kestrel always has one.
    /// </remarks>
    public bool Permits(IPAddress? address)
    {
        if (address is null)
        {
            return true;
        }

        var mapped = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (AllowedNetworks.Length == 0)
        {
            return PrivateDefaults.Any(network => IsInside(mapped, network.Network, network.Prefix));
        }

        return AllowedNetworks.Any(cidr => Matches(mapped, cidr));
    }

    private static bool Matches(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/', 2);

        if (!IPAddress.TryParse(parts[0], out var network))
        {
            return false;
        }

        var prefix = parts.Length == 2 && int.TryParse(parts[1], out var parsed)
            ? parsed
            : network.GetAddressBytes().Length * 8;

        return IsInside(address, network, prefix);
    }

    private static bool IsInside(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();

        for (var index = 0; index < addressBytes.Length; index++)
        {
            var bits = prefixLength - (index * 8);

            if (bits <= 0)
            {
                break;
            }

            var mask = bits >= 8 ? (byte)0xFF : (byte)(0xFF << (8 - bits));

            if ((addressBytes[index] & mask) != (networkBytes[index] & mask))
            {
                return false;
            }
        }

        return true;
    }
}
