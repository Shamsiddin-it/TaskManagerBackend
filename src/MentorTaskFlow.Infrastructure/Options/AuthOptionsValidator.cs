using System.Text;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Options;

/// <summary>
/// Refuses to start on an unusable auth configuration (<c>DEPLOY-015</c>).
/// </summary>
/// <remarks>
/// Failing at startup is the point: a service that boots with a weak or placeholder signing key would
/// happily issue forgeable tokens, and the problem would surface as a breach rather than as a failed
/// deployment.
/// </remarks>
public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    /// <summary>256 bits, the minimum for HS256 (<c>AUTH-001</c>).</summary>
    public const int MinimumSigningKeyBytes = 32;

    private static readonly string[] RejectedKeys =
    [
        "changeme",
        "secret",
        "development",
        "your-signing-key",
    ];

    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.JwtSigningKey))
        {
            failures.Add("Auth:JwtSigningKey is required (SEC-010: supply it from the secret manager).");
        }
        else
        {
            var keyBytes = Encoding.UTF8.GetByteCount(options.JwtSigningKey);

            if (keyBytes < MinimumSigningKeyBytes)
            {
                failures.Add(
                    $"Auth:JwtSigningKey must be at least {MinimumSigningKeyBytes} bytes " +
                    $"({MinimumSigningKeyBytes * 8} bits) for HS256; got {keyBytes}.");
            }

            if (RejectedKeys.Any(rejected =>
                    options.JwtSigningKey.Contains(rejected, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add("Auth:JwtSigningKey looks like a placeholder value; supply a real secret.");
            }
        }

        if (!Uri.TryCreate(options.AppBaseUrl, UriKind.Absolute, out var appBaseUrl))
        {
            failures.Add("Auth:AppBaseUrl must be an absolute URI — it is the base of password links.");
        }
        else if (appBaseUrl.Scheme is not ("http" or "https"))
        {
            failures.Add("Auth:AppBaseUrl must use http or https.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
