using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Admin;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Auditing;

/// <inheritdoc />
public sealed class AuditLogReader(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    IAuditWriter auditWriter) : IAuditLogReader
{
    public async Task<PagedResult<AuditLogEntryDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken)
    {
        var user = currentUser.Current ?? throw new UnauthorizedException();

        var page = Math.Max(query.Page, PaginationLimits.DefaultPage);
        var pageSize = Math.Clamp(query.PageSize, PaginationLimits.MinPageSize, PaginationLimits.MaxPageSize);

        // The explicit organization condition sits alongside the global query filter, never instead of
        // it: SEC-030 requires both, because a filter is dropped by IgnoreQueryFilters and does not
        // apply to raw SQL.
        var source = dbContext.AuditLogs
            .AsNoTracking()
            .Where(log => log.OrganizationId == branchContext.EffectiveOrganizationId);

        // A Branch Admin is pinned to their own branch, which also excludes the organization-level
        // rows (BranchId IS NULL) they must not see: those disclose the composition of the
        // organization and actions taken in other branches (TEN-049).
        if (user is { Role: UserRole.Admin, AdminScope: AdminScope.Branch })
        {
            source = source.Where(log => log.BranchId == user.BranchId);
        }
        else if (branchContext.EffectiveBranchId is { } selectedBranch)
        {
            source = source.Where(log => log.BranchId == selectedBranch);
        }

        source = ApplyFilters(source, query);

        // Computed under the very same predicate, so the counter cannot disclose the existence of
        // rows the caller may not read (TEN-002, API-012).
        var totalCount = await source.CountAsync(cancellationToken);

        var items = await source
            .OrderByDescending(log => log.OccurredAt)
            .ThenByDescending(log => log.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(log => new AuditLogEntryDto(
                log.Id,
                log.Action,
                log.EntityType,
                log.EntityId,
                log.BranchId,
                log.CategoryId,
                log.ActorType.ToString(),
                log.ActorId,
                log.ActorRole.HasValue ? log.ActorRole.Value.ToString() : null,
                log.ActorAdminScope.HasValue ? log.ActorAdminScope.Value.ToString() : null,
                log.Result.ToString(),
                log.FailureReason,
                log.HttpMethod,
                log.Path,
                log.OccurredAt,
                log.CorrelationId))
            .ToListAsync(cancellationToken);

        RecordRead(user);

        return new PagedResult<AuditLogEntryDto>(items, page, pageSize, totalCount);
    }

    private static IQueryable<AuditLog> ApplyFilters(IQueryable<AuditLog> source, AuditLogQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            source = source.Where(log => log.Action == query.Action);
        }

        if (query.ActorId is { } actorId)
        {
            source = source.Where(log => log.ActorId == actorId);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            source = source.Where(log => log.EntityType == query.EntityType);
        }

        if (query.EntityId is { } entityId)
        {
            source = source.Where(log => log.EntityId == entityId);
        }

        if (query.From is { } from)
        {
            source = source.Where(log => log.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            source = source.Where(log => log.OccurredAt <= to);
        }

        return source;
    }

    /// <summary>
    /// Records the read itself, naming the scope that was applied (<c>AUD-023</c>).
    /// </summary>
    /// <remarks>
    /// Reading an audit trail is an administrative action like any other, and knowing how much of it
    /// somebody looked at is exactly what an investigation needs.
    /// </remarks>
    private void RecordRead(ICurrentUserContext user)
    {
        var appliedBranchFilter = user is { Role: UserRole.Admin, AdminScope: AdminScope.Branch }
            ? user.BranchId?.ToString()
            : branchContext.EffectiveBranchId?.ToString() ?? "all";

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.AuditRead,
            EntityType = nameof(AuditLog),
            Metadata = JsonSerializer.SerializeToDocument(new
            {
                branchFilter = appliedBranchFilter,
                adminScope = user.AdminScope?.ToString(),
            }),
        });
    }
}
