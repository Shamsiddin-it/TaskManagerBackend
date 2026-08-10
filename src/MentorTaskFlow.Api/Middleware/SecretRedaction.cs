using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace MentorTaskFlow.Api.Middleware;

/// <summary>
/// Centralized redaction of secrets in the technical log (<c>SEC-022</c>, <c>SEC-023</c>).
/// </summary>
/// <remarks>
/// <para>
/// One enricher rather than filtering at each call site, exactly as <c>SEC-022</c> requires. Manual
/// filtering fails the first time somebody adds a log statement and forgets — and the failure is
/// invisible until an audit finds a token sitting in a log file.
/// </para>
/// <para>
/// Never permitted anywhere in logs, AuditLog, TaskEvent, metrics or traces: passwords and their
/// hashes, security tokens, refresh tokens, Telegram bind tokens, whole JWTs, the <c>Authorization</c>
/// header, presigned URLs, uploaded file contents, the Anthropic key, the Telegram bot token, the
/// webhook secret and the database connection string (<c>SEC-021</c>).
/// </para>
/// </remarks>
public sealed partial class SecretRedactionEnricher : ILogEventEnricher
{
    public const string RedactedValue = "***";

    /// <summary>Headers redacted in full — the value has no safe prefix.</summary>
    private static readonly string[] RedactedProperties =
    [
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "X-CSRF-Token",
        "X-Telegram-Bot-Api-Secret-Token",
        "Password",
        "NewPassword",
        "CurrentPassword",
        "Token",
        "RefreshToken",
        "AccessToken",
        "TokenHash",
        "PasswordHash",
        "ConnectionString",
        "ApiKey",
        "BotToken",
        "SigningKey",
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var name in RedactedProperties)
        {
            if (logEvent.Properties.ContainsKey(name))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(RedactedValue)));
            }
        }

        // Query strings reach the log through RequestPath and through Serilog's request logging.
        // `token`, `code`, `signature` and the S3 signature parameters lose their values while the
        // parameter name stays, so a request is still recognisable in an incident review (SEC-023).
        RedactQueryString(logEvent, "RequestPath");
        RedactQueryString(logEvent, "Path");
    }

    private static void RedactQueryString(LogEvent logEvent, string propertyName)
    {
        if (!logEvent.Properties.TryGetValue(propertyName, out var property)
            || property is not ScalarValue { Value: string raw }
            || !raw.Contains('?'))
        {
            return;
        }

        var redacted = SensitiveQueryParameter().Replace(raw, $"$1={RedactedValue}");

        if (!string.Equals(redacted, raw, StringComparison.Ordinal))
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(propertyName, new ScalarValue(redacted)));
        }
    }

    [GeneratedRegex(
        @"\b(token|code|signature|X-Amz-Signature|X-Amz-Credential)=[^&\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryParameter();
}

/// <summary>
/// Suppresses request bodies for the endpoints that carry credentials (<c>SEC-023</c>).
/// </summary>
/// <remarks>
/// Bodies of <c>/auth/*</c> and <c>/telegram/bind-token</c> are not logged at all. Redacting field by
/// field would work only for the fields somebody remembered to name; refusing the whole body needs no
/// such list.
/// </remarks>
public static class SensitiveEndpoints
{
    private static readonly string[] Paths =
    [
        "/api/v1/auth",
        "/api/v1/telegram/bind-token",
    ];

    public static bool CarriesCredentials(PathString path) =>
        Paths.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
