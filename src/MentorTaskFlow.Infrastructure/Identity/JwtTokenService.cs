using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Security;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MentorTaskFlow.Infrastructure.Identity;

/// <inheritdoc />
public sealed class JwtTokenService(IOptions<AuthOptions> options, IClock clock) : IJwtTokenService
{
    private readonly AuthOptions _options = options.Value;

    public AccessToken Issue(User user)
    {
        var now = clock.UtcNow;
        var expiresAt = now.Add(_options.AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(MtfClaimTypes.Subject, user.Id.ToString()),
            new(MtfClaimTypes.Role, user.Role.ToString()),
            new(MtfClaimTypes.TokenVersion, user.TokenVersion.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),

            // org_id is present in every token of every user type; a token without it is invalid
            // (AUTH-033).
            new(MtfClaimTypes.OrganizationId, user.OrganizationId.ToString()),
        };

        // A claim that does not apply to the role is omitted entirely — null is never written into
        // the token (AUTH-031). The middleware then rejects any claim set matching no row of that
        // table, which is only meaningful if absence really means absence.
        if (user.AdminScope is { } adminScope)
        {
            claims.Add(new Claim(MtfClaimTypes.AdminScope, adminScope.ToString()));
        }

        if (user.BranchId is { } branchId)
        {
            claims.Add(new Claim(MtfClaimTypes.BranchId, branchId.ToString()));
        }

        if (user.CategoryId is { } categoryId)
        {
            claims.Add(new Claim(MtfClaimTypes.CategoryId, categoryId.ToString()));
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.JwtIssuer,
            audience: _options.JwtAudience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
