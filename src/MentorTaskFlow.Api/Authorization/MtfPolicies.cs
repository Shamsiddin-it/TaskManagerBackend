using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;

namespace MentorTaskFlow.Api.Authorization;

/// <summary>
/// The authorization policies of TZ 8 and Приложение A.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint carries an explicitly named policy; one without is not admitted to merge
/// (<c>SEC-001</c>), and <c>EndpointPolicyTests</c> enforces that.
/// </para>
/// <para>
/// A policy is level 5 of the six-level model — it answers «may this role act at all», not «may this
/// role touch <b>this</b> object». Levels 1–4 (organization, branch, category, ownership) are explicit
/// conditions inside each handler, because a policy cannot see the object being addressed
/// (<c>SEC-002</c>, <c>SEC-030</c>).
/// </para>
/// <para>
/// The administrative contour is expressed by the pair (<c>Role=Admin</c>, <c>AdminScope</c>) and never
/// by a new role: additional roles would duplicate the whole permission matrix without granting a
/// single new permission (<c>TEN-011</c>).
/// </para>
/// </remarks>
public static class MtfPolicies
{
    /// <summary>Any authenticated user of any role.</summary>
    public const string Authenticated = "mtf.authenticated";

    /// <summary><c>Role=Admin</c> and <c>AdminScope=Organization</c>.</summary>
    public const string OrganizationAdmin = "mtf.organization-admin";

    /// <summary><c>Role=Admin</c> and <c>AdminScope=Branch</c>.</summary>
    public const string BranchAdmin = "mtf.branch-admin";

    /// <summary>Either administrative contour. Object-level reach still differs and is checked in the handler.</summary>
    public const string AnyAdmin = "mtf.any-admin";

    public const string Lead = "mtf.lead";

    public const string Mentor = "mtf.mentor";

    /// <summary>Lead or either Admin — the shape of most configuration endpoints.</summary>
    public const string LeadOrAdmin = "mtf.lead-or-admin";

    public static IServiceCollection AddMentorTaskFlowAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(Authenticated, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(OrganizationAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsAdminWith(context, AdminScope.Organization)))
            .AddPolicy(BranchAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsAdminWith(context, AdminScope.Branch)))
            .AddPolicy(AnyAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(MtfClaimTypes.Role, nameof(UserRole.Admin)))
            .AddPolicy(Lead, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(MtfClaimTypes.Role, nameof(UserRole.Lead)))
            .AddPolicy(Mentor, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(MtfClaimTypes.Role, nameof(UserRole.Mentor)))
            .AddPolicy(LeadOrAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(MtfClaimTypes.Role, nameof(UserRole.Admin), nameof(UserRole.Lead)));

        return services;
    }

    /// <summary>
    /// Reads the contour straight from the claims.
    /// </summary>
    /// <remarks>
    /// Safe because the claim set was already checked against the <c>AUTH-031</c> table before any
    /// policy ran: a token where <c>admin_scope</c> is present without <c>Role=Admin</c>, or absent
    /// with it, is refused at authentication time (<c>AUTH-032</c>).
    /// </remarks>
    private static bool IsAdminWith(AuthorizationHandlerContext context, AdminScope scope) =>
        context.User.HasClaim(MtfClaimTypes.Role, nameof(UserRole.Admin))
        && context.User.HasClaim(MtfClaimTypes.AdminScope, scope.ToString());
}
