namespace MentorTaskFlow.Infrastructure.Storage;

/// <summary>
/// The host and port of the storage endpoint, however the configuration spells it.
/// </summary>
/// <remarks>
/// <para>
/// The MinIO client takes a host and a port and decides the scheme itself from <c>WithSSL</c>; the
/// configured <c>Storage:Endpoint</c> is a URL, because that is what the AWS SDK wanted and what every
/// deployment already has written down. Rather than change the setting under running installations,
/// the URL is parsed here and the scheme is dropped.
/// </para>
/// <para>
/// A bare <c>host:port</c> is accepted too, so a deployment that writes the endpoint the way MinIO's
/// own documentation does is not punished for it.
/// </para>
/// </remarks>
public readonly record struct StorageEndpoint(string Host, int Port)
{
    private const int DefaultHttpPort = 80;
    private const int DefaultHttpsPort = 443;

    public static StorageEndpoint Parse(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var value = endpoint.Trim();

        // Uri needs a scheme to parse an authority at all; a bare host:port gets a placeholder that
        // never leaves this method — the scheme comes from Storage:UseSsl, not from here.
        var uri = value.Contains("://", StringComparison.Ordinal)
            ? new Uri(value)
            : new Uri($"http://{value}");

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException($"Storage:Endpoint '{endpoint}' has no host.");
        }

        // Uri fills in the default port for the scheme when none was written. That default belongs to
        // the scheme in the string, which may not be the scheme the client will use, so it is only
        // taken when the endpoint actually named a port.
        var port = uri.IsDefaultPort && !HasExplicitPort(value)
            ? uri.Scheme == Uri.UriSchemeHttps ? DefaultHttpsPort : DefaultHttpPort
            : uri.Port;

        return new StorageEndpoint(uri.Host, port);
    }

    private static bool HasExplicitPort(string value)
    {
        var authority = value.Contains("://", StringComparison.Ordinal)
            ? value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..]
            : value;

        var host = authority.Split('/', 2)[0];

        return host.LastIndexOf(':') > host.LastIndexOf(']');
    }
}
