using System.Security.Claims;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Security;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace MentorTaskFlow.Api.Authentication;

/// <summary>
/// Applies the token checks of <c>AUTH-004</c>, <c>AUTH-032</c> and <c>AUTH-028</c>, and turns every
/// rejection into the catalog code the client expects.
/// </summary>
/// <remarks>
/// Signature, issuer, audience and expiry are handled by the handler itself. What remains — and what
/// a stock configuration would miss — is that a validly signed token can still be unusable: its claim
/// set may be impossible for any stored user, or its <c>tv</c> may have been superseded seconds ago.
/// </remarks>
public sealed class MtfJwtBearerEvents : JwtBearerEvents
{
    private const string FailureCodeKey = "mtf.auth.failure-code";

    public MtfJwtBearerEvents()
    {
        OnTokenValidated = ValidateAsync;
        OnChallenge = ChallengeAsync;
    }

    private static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;

        if (principal is null || !TryReadShape(principal, out var userId, out var tokenVersion))
        {
            Reject(context.HttpContext, ErrorCodes.Unauthorized);
            context.Fail("Claim set does not match any permitted shape (AUTH-032).");
            return;
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<ITokenVersionValidator>();
        var check = await validator.CheckAsync(userId, tokenVersion, context.HttpContext.RequestAborted);

        switch (check)
        {
            case TokenVersionCheck.Valid:
                return;

            case TokenVersionCheck.Deactivated:
                Reject(context.HttpContext, ErrorCodes.UserDeactivated);
                context.Fail("The account is deactivated (USER-004).");
                return;

            default:
                Reject(context.HttpContext, ErrorCodes.TokenVersionMismatch);
                context.Fail("TokenVersion has been superseded (AUTH-028).");
                return;
        }
    }

    /// <summary>
    /// Verifies that the claim set matches one row of the <c>AUTH-031</c> table.
    /// </summary>
    /// <remarks>
    /// The check runs before any business logic and never reveals which claim was wrong
    /// (<c>AUTH-032</c>). Reusing <see cref="User.EnsureScopeShape"/> is the point: the token, the
    /// CHECK constraint <c>ck_users_scope_shape</c> and the domain factories all express one rule, so
    /// a token describing a user who could not exist is refused rather than acted upon.
    /// </remarks>
    private static bool TryReadShape(ClaimsPrincipal principal, out Guid userId, out int tokenVersion)
    {
        userId = default;
        tokenVersion = default;

        if (!Guid.TryParse(principal.FindFirstValue(MtfClaimTypes.Subject), out userId)
            || !Guid.TryParse(principal.FindFirstValue(MtfClaimTypes.OrganizationId), out _)
            || !Enum.TryParse<UserRole>(principal.FindFirstValue(MtfClaimTypes.Role), out var role)
            || !int.TryParse(principal.FindFirstValue(MtfClaimTypes.TokenVersion), out tokenVersion))
        {
            return false;
        }

        AdminScope? adminScope = null;
        var rawAdminScope = principal.FindFirstValue(MtfClaimTypes.AdminScope);

        if (!string.IsNullOrEmpty(rawAdminScope))
        {
            if (!Enum.TryParse<AdminScope>(rawAdminScope, out var parsed))
            {
                return false;
            }

            adminScope = parsed;
        }

        Guid? branchId = Guid.TryParse(principal.FindFirstValue(MtfClaimTypes.BranchId), out var branch) ? branch : null;
        Guid? categoryId = Guid.TryParse(principal.FindFirstValue(MtfClaimTypes.CategoryId), out var category) ? category : null;

        try
        {
            User.EnsureScopeShape(role, adminScope, branchId, categoryId);
            return true;
        }
        catch (Domain.Common.DomainException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces the handler's bare <c>WWW-Authenticate</c> response with a ProblemDetails body
    /// carrying the catalog code.
    /// </summary>
    /// <remarks>
    /// Throwing rather than writing here is intentional: the exception middleware sits earlier in the
    /// pipeline, so the response is produced by the one component that knows how to shape a
    /// ProblemDetails, and 401s look identical to every other error (TZ 26.1).
    /// </remarks>
    private static Task ChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();

        var code = context.HttpContext.Items.TryGetValue(FailureCodeKey, out var stored) && stored is string storedCode
            ? storedCode
            : context.AuthenticateFailure switch
            {
                SecurityTokenExpiredException => ErrorCodes.TokenExpired,
                _ => ErrorCodes.Unauthorized,
            };

        throw new UnauthorizedException(code, DetailFor(code));
    }

    private static void Reject(HttpContext context, string code) => context.Items[FailureCodeKey] = code;

    private static string DetailFor(string code) => code switch
    {
        ErrorCodes.TokenExpired => "Срок действия токена истёк.",
        ErrorCodes.TokenVersionMismatch => "Токен более не действителен, требуется повторный вход.",
        ErrorCodes.UserDeactivated => "Учётная запись деактивирована.",
        _ => "Требуется аутентификация.",
    };
}
