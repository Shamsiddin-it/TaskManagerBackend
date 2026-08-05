using System.Security.Claims;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Domain.Tenancy;

namespace MentorTaskFlow.Api.Tenancy;

/// <summary>
/// Builds <see cref="ICurrentUserContext"/> from the validated access token.
/// </summary>
/// <remarks>
/// A claim set that matches no row of the <c>AUTH-031</c> table yields no principal at all, so callers
/// treat the request as unauthenticated. The full rejection with 401 belongs to the authentication
/// middleware of Phase 2 (<c>AUTH-032</c>); refusing to construct a half-valid context here means no
/// business code can ever observe, say, a Lead without a branch.
/// </remarks>
public sealed class HttpCurrentUserAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentUserAccessor
{
    private ICurrentUserContext? _cached;
    private bool _resolved;

    public ICurrentUserContext? Current
    {
        get
        {
            if (_resolved)
            {
                return _cached;
            }

            _resolved = true;
            _cached = Resolve(httpContextAccessor.HttpContext?.User);
            return _cached;
        }
    }

    public bool IsAuthenticated => Current is not null;

    private static ICurrentUserContext? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return null;
        }

        if (!TryGetGuid(principal, MtfClaimTypes.Subject, out var userId)
            || !TryGetGuid(principal, MtfClaimTypes.OrganizationId, out var organizationId)
            || !Enum.TryParse<UserRole>(principal.FindFirstValue(MtfClaimTypes.Role), out var role)
            || !int.TryParse(principal.FindFirstValue(MtfClaimTypes.TokenVersion), out var tokenVersion))
        {
            return null;
        }

        AdminScope? adminScope = null;
        var rawAdminScope = principal.FindFirstValue(MtfClaimTypes.AdminScope);
        if (!string.IsNullOrEmpty(rawAdminScope))
        {
            if (!Enum.TryParse<AdminScope>(rawAdminScope, out var parsed))
            {
                return null;
            }

            adminScope = parsed;
        }

        Guid? branchId = TryGetGuid(principal, MtfClaimTypes.BranchId, out var branch) ? branch : null;
        Guid? categoryId = TryGetGuid(principal, MtfClaimTypes.CategoryId, out var category) ? category : null;

        // The very invariant that CHECK ck_users_scope_shape enforces in the database, applied to the
        // token. A token whose shape is impossible for a stored user is not a valid token.
        try
        {
            Domain.Users.User.EnsureScopeShape(role, adminScope, branchId, categoryId);
        }
        catch (Domain.Common.DomainException)
        {
            return null;
        }

        return new CurrentUserContext(userId, role, adminScope, organizationId, branchId, categoryId, tokenVersion);
    }

    private static bool TryGetGuid(ClaimsPrincipal principal, string claimType, out Guid value)
    {
        var raw = principal.FindFirstValue(claimType);
        return Guid.TryParse(raw, out value);
    }

    private sealed record CurrentUserContext(
        Guid UserId,
        UserRole Role,
        AdminScope? AdminScope,
        Guid OrganizationId,
        Guid? BranchId,
        Guid? CategoryId,
        int TokenVersion) : ICurrentUserContext;
}
