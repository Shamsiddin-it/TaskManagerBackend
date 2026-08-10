using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Infrastructure.Persistence;

namespace MentorTaskFlow.Infrastructure.Notifications;

/// <inheritdoc />
public sealed class OutboxWriter(
    MentorTaskFlowDbContext dbContext,
    IBranchContext branchContext,
    IClock clock) : IOutboxWriter
{
    public void Enqueue(OutboxEntry entry)
    {
        var branchId = NotificationEventTypes.OrganizationLevelEvents.Contains(entry.EventType)
            ? null
            : branchContext.EffectiveBranchId;

        Add(entry, branchContext.EffectiveOrganizationId, branchId);
    }

    public void EnqueueSystem(OutboxEntry entry, Guid organizationId, Guid? branchId) =>
        Add(entry, organizationId, branchId);

    private void Add(OutboxEntry entry, Guid organizationId, Guid? branchId)
    {
        dbContext.NotificationOutbox.Add(NotificationOutbox.Enqueue(
            entry.RecipientUserId,
            organizationId,
            branchId,
            entry.CategoryId,
            entry.Channel,
            entry.EventType,
            entry.Payload,
            BuildDeduplicationKey(organizationId, branchId, entry),
            clock.UtcNow,
            entry.IsSystemAlert));
    }

    /// <summary>
    /// Prefixes the caller's key with the tenant scope (<c>NTF-015</c>).
    /// </summary>
    /// <remarks>
    /// Mandatory, not decorative. Without it, `category-no-lead` for the `C#` category of the head
    /// office and the same event for the `C#` category of the Khujand branch would produce identical
    /// keys, collide on <c>ux_notification_outbox_dedup</c>, and one branch's notification would
    /// <b>silently suppress</b> the other's — the most dangerous class of defect, because it never
    /// surfaces as an error (<c>TEN-043</c>).
    /// </remarks>
    private static string BuildDeduplicationKey(Guid organizationId, Guid? branchId, OutboxEntry entry) =>
        $"{organizationId:N}:{(branchId is { } id ? id.ToString("N") : "org")}:{entry.DeduplicationKey}";
}
