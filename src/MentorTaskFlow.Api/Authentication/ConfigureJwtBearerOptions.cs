using System.Text;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MentorTaskFlow.Api.Authentication;

/// <summary>
/// Builds the bearer validation parameters from <see cref="AuthOptions"/> (TZ 16.1).
/// </summary>
/// <remarks>
/// Resolved from DI rather than read straight off <c>builder.Configuration</c> during startup. An
/// eager <c>.Get&lt;AuthOptions&gt;()</c> takes a snapshot before every configuration source has been
/// added — which silently produced an empty signing key under
/// <c>WebApplicationFactory</c> — whereas an options configurator runs after the container is built
/// and always sees the final values.
/// </remarks>
public sealed class ConfigureJwtBearerOptions(IOptions<AuthOptions> authOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly AuthOptions _options = authOptions.Value;

    public void Configure(string? name, JwtBearerOptions options) => Configure(options);

    public void Configure(JwtBearerOptions options)
    {
        // Off, so `role` and `sub` arrive under the names the token actually uses. With the default
        // mapping they would be rewritten to the long WS-Federation URIs and MtfClaimTypes would
        // stop matching.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.JwtIssuer,
            ValidateAudience = true,
            ValidAudience = _options.JwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSigningKey)),
            ValidateLifetime = true,

            // AUTH-004: at most 30 seconds of drift. The framework default is five minutes, which
            // would stretch the honest revocation window of section 16.7 tenfold.
            ClockSkew = TimeSpan.FromSeconds(_options.ClockSkewSeconds),

            RoleClaimType = MtfClaimTypes.Role,
            NameClaimType = MtfClaimTypes.Subject,
        };

        options.EventsType = typeof(MtfJwtBearerEvents);
    }
}
