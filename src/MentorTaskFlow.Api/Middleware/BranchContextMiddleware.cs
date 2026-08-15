using System.Text.Json;
using MentorTaskFlow.Api.Options;
using MentorTaskFlow.Api.Tenancy;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Api.Middleware;

/// <summary>
/// Computes the effective scope of the request and publishes it to <see cref="IBranchContext"/> and
/// to the EF Core query filters (TZ 38.3).
/// </summary>
/// <remarks>
/// <para>
/// <c>EffectiveOrganizationId</c> always comes from the authenticated principal. No input — body,
/// query string, header or route — can change it; this rule has no exceptions (<c>TEN-030a</c>).
/// </para>
/// <para>
/// The <c>X-MTF-Branch-Id</c> header is a client's <b>request to narrow visibility</b>, never an
/// assertion of rights. It is accepted only from an Organization Admin, only after the branch is
/// confirmed to belong to their organization, and it is re-validated on every request because a
/// branch may have been deactivated or an admin's contour changed since the last one
/// (<c>TEN-035a</c>, <c>TEN-036a</c>).
/// </para>
/// </remarks>
public sealed class BranchContextMiddleware(RequestDelegate next, IOptions<TenancyOptions> options)
{
    private readonly TenancyOptions _options = options.Value;

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUserAccessor currentUserAccessor,
        IBranchContext branchContext,
        TenantFilterState tenantFilter,
        IBranchScopeValidator branchScopeValidator,
        TenancyMetrics metrics)
    {
        var hasBranchHeader = context.Request.Headers.TryGetValue(_options.BranchHeaderName, out var rawBranchId);
        var user = currentUserAccessor.Current;

        if (user is null)
        {
            // Anonymous endpoints only (login, refresh, password reset, Telegram webhook). The tenant
            // filter stays fail-closed, so an authenticated-data query on this path returns nothing
            // rather than everything.
            await next(context);
            return;
        }

        var isOrganizationAdmin = user is { Role: UserRole.Admin, AdminScope: AdminScope.Organization };

        if (hasBranchHeader && !isOrganizationAdmin)
        {
            // Rejected even when the value equals the caller's own branch. Any presence of the header
            // from these roles is either a client defect or a bypass attempt, and both warrant
            // observation (TEN-032).
            metrics.RecordBranchScopeDenied(user.OrganizationId, user.BranchId);
            await RecordScopeOverrideRejectionAsync(context, user, cancellationToken: context.RequestAborted);

            throw new ForbiddenException(
                ErrorCodes.ScopeOverrideForbidden,
                "Заголовок выбора филиала недопустим для данной роли.");
        }

        Guid? effectiveBranchId;

        if (isOrganizationAdmin)
        {
            effectiveBranchId = hasBranchHeader
                ? await ResolveRequestedBranchAsync(rawBranchId!, user.OrganizationId, branchScopeValidator, context.RequestAborted)
                : null;
        }
        else
        {
            // Branch Admin, Lead and Mentor are pinned to the branch in their token and cannot widen
            // or narrow it (TEN-031a).
            effectiveBranchId = user.BranchId;
        }

        if (branchContext is RequestBranchContext requestBranchContext)
        {
            requestBranchContext.Establish(
                user.OrganizationId,
                effectiveBranchId,
                canOverrideBranch: isOrganizationAdmin,
                onMissingBranchForMutation: () => metrics.RecordBranchContextMissing(user.OrganizationId));
        }

        tenantFilter.SetScope(user.OrganizationId, effectiveBranchId);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[TenancyOptions.EffectiveBranchHeaderName] =
                effectiveBranchId?.ToString() ?? TenancyOptions.AllBranchesHeaderValue;
            return Task.CompletedTask;
        });

        await next(context);
    }

    /// <summary>
    /// Records the rejected override attempt (<c>AUD-020</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as a system action and committed on its own, before the 403 propagates. The request is
    /// about to be aborted, so there is no later unit of work to ride along with, and a security
    /// event that vanishes with the failed request is worthless.
    /// </para>
    /// <para>
    /// A failure to record must not mask the 403 itself, so the write is best-effort: the rejection is
    /// the security outcome that matters, the audit row is evidence about it.
    /// </para>
    /// </remarks>
    private static async Task RecordScopeOverrideRejectionAsync(
        HttpContext context,
        ICurrentUserContext user,
        CancellationToken cancellationToken)
    {
        try
        {
            var auditWriter = context.RequestServices.GetRequiredService<IAuditWriter>();
            var unitOfWork = context.RequestServices.GetRequiredService<IUnitOfWork>();

            auditWriter.WriteSystem(
                new AuditEntry
                {
                    Action = AuditActions.SecurityScopeOverrideRejected,
                    EntityType = nameof(Branch),
                    Result = AuditResult.Failure,
                    FailureReason = ErrorCodes.ScopeOverrideForbidden,
                    Metadata = JsonSerializer.SerializeToDocument(new
                    {
                        actorRole = user.Role.ToString(),
                        actorAdminScope = user.AdminScope?.ToString(),
                    }),
                },
                user.OrganizationId,

                // Branch-scoped for these roles, so a Branch Admin can see attempts made against their
                // own branch. The organization-level variant of TEN-048 applies when the actor is an
                // Organization Admin, which cannot reach this branch of the code.
                user.BranchId);

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            context.RequestServices
                .GetRequiredService<ILogger<BranchContextMiddleware>>()
                .LogError(exception, "Failed to record a rejected scope-override attempt.");
        }
    }

    private static async Task<Guid> ResolveRequestedBranchAsync(
        string rawBranchId,
        Guid organizationId,
        IBranchScopeValidator validator,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(rawBranchId, out var branchId))
        {
            throw new ValidationAppException(
                "X-MTF-Branch-Id",
                "Значение заголовка должно быть корректным UUID.");
        }

        if (!await validator.BelongsToOrganizationAsync(branchId, organizationId, cancellationToken))
        {
            // 404, not 403 — a distinct code would confirm that a branch of another organization
            // exists (TEN-007, TEN-032a).
            throw new NotFoundException();
        }

        return branchId;
    }
}

public static class BranchContextMiddlewareExtensions
{
    public static IApplicationBuilder UseBranchContext(this IApplicationBuilder app) =>
        app.UseMiddleware<BranchContextMiddleware>();
}
