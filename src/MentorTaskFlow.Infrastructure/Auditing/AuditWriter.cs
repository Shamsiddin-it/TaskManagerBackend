using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Infrastructure.Persistence;

namespace MentorTaskFlow.Infrastructure.Auditing;

/// <inheritdoc />
/// <remarks>
/// Scope, actor and correlation id come from the request context, never from the caller
/// (<c>TEN-022</c>). Rows are added to the change tracker and committed by whoever owns the
/// transaction, so an audit record cannot outlive a rolled-back action.
/// </remarks>
public sealed class AuditWriter(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    IRequestContext requestContext,
    IClock clock) : IAuditWriter
{
    public void Write(AuditEntry entry)
    {
        var user = currentUser.Current
            ?? throw new InvalidOperationException(
                "IAuditWriter.Write requires an authenticated principal; use WriteSystem for background tasks.");

        // An always-organization-level action is recorded without a branch even when one is selected:
        // creating a branch is not an event "in" the branch that happened to be in context.
        //
        // Otherwise the caller may name the subject's branch explicitly — an operation on a user or a
        // category is an event in *their* branch, whichever one the acting Organization Admin had
        // selected — and the request's effective branch is the fallback. A null that reaches
        // AuditLog.Record is caught there and again by ck_audit_logs_branch_scope.
        var branchId = AuditActions.AlwaysOrganizationLevelActions.Contains(entry.Action)
            ? null
            : entry.BranchId ?? branchContext.EffectiveBranchId;

        dbContext.AuditLogs.Add(AuditLog.Record(
            entry.Action,
            entry.EntityType,
            branchContext.EffectiveOrganizationId,
            branchId,
            entry.CategoryId,
            entry.EntityId,
            AuditActorType.User,
            user.UserId,
            user.Role,
            user.AdminScope,
            entry.Result,
            requestContext.CorrelationId,
            clock.UtcNow,
            requestContext.HttpMethod,
            requestContext.Path,
            requestContext.IpAddress,
            requestContext.UserAgent,
            entry.FailureReason,
            entry.Metadata));
    }

    public void WriteSystem(AuditEntry entry, Guid organizationId, Guid? branchId, Guid? correlationId = null)
    {
        dbContext.AuditLogs.Add(AuditLog.Record(
            entry.Action,
            entry.EntityType,
            organizationId,
            branchId,
            entry.CategoryId,
            entry.EntityId,
            AuditActorType.System,

            // System actions carry no actor: AuditLog.Record rejects an ActorId here, and
            // ck_audit_logs_actor_shape rejects it again in the database.
            actorId: null,
            actorRole: null,
            actorAdminScope: null,
            entry.Result,
            correlationId ?? requestContext.CorrelationId,
            clock.UtcNow,
            failureReason: entry.FailureReason,
            metadata: entry.Metadata));
    }
}
