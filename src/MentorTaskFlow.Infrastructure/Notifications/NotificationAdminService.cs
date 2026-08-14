using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Notifications;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Notifications;

/// <inheritdoc />
public sealed class NotificationAdminService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    IAuditWriter auditWriter,
    IClock clock) : INotificationAdminService
{
    public async Task<PagedResult<NotificationDto>> ListAsync(
        NotificationListQuery query,
        CancellationToken cancellationToken)
    {
        var actor = RequireAdmin();

        var page = Math.Max(query.Page, PaginationLimits.DefaultPage);
        var pageSize = Math.Clamp(query.PageSize, PaginationLimits.MinPageSize, PaginationLimits.MaxPageSize);

        var source = Visible(actor);

        if (!string.IsNullOrWhiteSpace(query.Status) && Enum.TryParse<NotificationStatus>(query.Status, out var status))
        {
            source = source.Where(n => n.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Channel) && Enum.TryParse<NotificationChannel>(query.Channel, out var channel))
        {
            source = source.Where(n => n.Channel == channel);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            source = source.Where(n => n.EventType == query.EventType);
        }

        // Computed under the same predicate as the rows: a counter that saw more than the list would
        // disclose the volume of another branch's traffic (TEN-046).
        var totalCount = await source.CountAsync(cancellationToken);

        var items = await source
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => ToDto(n))
            .ToListAsync(cancellationToken);

        return new PagedResult<NotificationDto>(items, page, pageSize, totalCount);
    }

    public async Task<NotificationDto> RetryAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        var actor = RequireAdmin();

        var row = await Visible(actor, tracked: true)
            .FirstOrDefaultAsync(n => n.Id == notificationId, cancellationToken)
            ?? throw new NotFoundException();

        // NTF-014: only from the dead letter. A Pending or Processing row is already on its way, and
        // resetting it would produce a second delivery of the same message.
        row.RequeueByAdmin(clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.NotificationRetry,
            EntityType = nameof(NotificationOutbox),
            EntityId = row.Id,
            BranchId = row.BranchId,
            CategoryId = row.CategoryId,
            Metadata = JsonSerializer.SerializeToDocument(new { eventType = row.EventType }),
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(row);
    }

    /// <summary>
    /// Narrows the journal to the caller's administrative contour (<c>TEN-046</c>, <c>TEN-047</c>).
    /// </summary>
    /// <remarks>
    /// A Branch Admin sees their branch and — deliberately — none of the organization-level rows: those
    /// concern branches other than theirs and would disclose the composition of the organization. An
    /// Organization Admin sees everything, narrowed by <c>X-MTF-Branch-Id</c> when they choose a
    /// branch; the global query filter already applies that half.
    /// </remarks>
    private IQueryable<NotificationOutbox> Visible(ICurrentUserContext actor, bool tracked = false)
    {
        var source = tracked ? dbContext.NotificationOutbox : dbContext.NotificationOutbox.AsNoTracking();

        source = source.Where(n => n.OrganizationId == branchContext.EffectiveOrganizationId);

        return actor is { Role: UserRole.Admin, AdminScope: AdminScope.Branch }
            ? source.Where(n => n.BranchId == actor.BranchId)
            : source;
    }

    private ICurrentUserContext RequireAdmin()
    {
        var actor = currentUser.Current ?? throw new UnauthorizedException();

        return actor.Role is UserRole.Admin
            ? actor
            : throw new ForbiddenException(ErrorCodes.Forbidden, "Журнал уведомлений доступен только администратору.");
    }

    private static NotificationDto ToDto(NotificationOutbox row) => new(
        row.Id,
        row.UserId,
        row.BranchId,
        row.CategoryId,
        row.EventType,
        row.Channel.ToString(),
        row.Status.ToString(),
        row.Attempts,
        row.NextAttemptAt,
        row.LastAttemptAt,
        row.SentAt,
        row.LastError,
        row.IsSystemAlert,
        row.CreatedAt);
}
