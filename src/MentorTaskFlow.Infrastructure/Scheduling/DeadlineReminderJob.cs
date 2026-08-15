using System.Text.Json;
using Hangfire;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MentorTaskFlow.Infrastructure.Scheduling;

/// <summary>
/// Reminds mentors of deadlines coming up (TZ 18.3).
/// </summary>
/// <remarks>
/// Runs every fifteen minutes and selects work still open whose deadline falls inside the category's
/// reminder window. A task already submitted, under review, approved, cancelled or overdue is skipped:
/// the reminder is about acting before the deadline, and none of those states leaves anything to do
/// (<c>NTF-005</c>).
/// </remarks>
public sealed class DeadlineReminderJob(
    MentorTaskFlowDbContext dbContext,
    IOutboxWriter outboxWriter,
    SchedulerMetrics metrics,
    ILogger<DeadlineReminderJob> logger,
    IClock clock)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // TEN-052: bounded by active organizations, active branches and the settings of the category
        // the work belongs to. Reminders for a deactivated branch are not created.
        var due = await dbContext.Assignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.Status == AssignmentStatus.Assigned || a.Status == AssignmentStatus.NeedsRework)
            .Where(a => a.CurrentDueAt > now)
            .Join(
                dbContext.CategorySettings.IgnoreQueryFilters().AsNoTracking(),
                a => a.CategoryId,
                s => s.CategoryId,
                (a, s) => new { Assignment = a, s.DeadlineReminderHours })
            .Join(
                dbContext.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.IsActive),
                x => x.Assignment.BranchId,
                b => b.Id,
                (x, b) => x)
            .Join(
                dbContext.Organizations.IgnoreQueryFilters().AsNoTracking().Where(o => o.IsActive),
                x => x.Assignment.OrganizationId,
                o => o.Id,
                (x, o) => x)
            .Where(x => x.Assignment.CurrentDueAt <= now.AddHours(x.DeadlineReminderHours))
            .Select(x => new ReminderRow(
                x.Assignment.Id,
                x.Assignment.OrganizationId,
                x.Assignment.BranchId,
                x.Assignment.CategoryId,
                x.Assignment.AssignedToId,
                x.Assignment.Title,
                x.Assignment.CurrentDueAt))
            .ToListAsync(cancellationToken);

        foreach (var row in due)
        {
            await EnqueueAsync(row, cancellationToken);
        }

        if (due.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Queued {Count} deadline reminder(s).", due.Count);
        }
    }

    /// <summary>
    /// <c>NTF-004</c>: the key carries the deadline value itself.
    /// </summary>
    /// <remarks>
    /// That single choice gives three properties at once: exactly one reminder per deadline however
    /// often the job runs; a genuinely new reminder after rework moves the deadline; and no duplicate
    /// of the old one. A key without the value would need all three handled separately.
    /// </remarks>
    private async Task EnqueueAsync(ReminderRow row, CancellationToken cancellationToken)
    {
        await outboxWriter.EnqueueSystemAsync(
            new OutboxEntry
            {
                RecipientUserId = row.AssignedToId,
                EventType = NotificationEventTypes.DeadlineReminder,
                EntityId = row.Id,
                Discriminator = row.CurrentDueAt.ToString("O"),
                CategoryId = row.CategoryId,
                Payload = JsonSerializer.SerializeToDocument(new
                {
                    assignmentTitle = row.Title,
                    currentDueAt = row.CurrentDueAt,
                }),
            },
            row.OrganizationId,
            row.BranchId,
            cancellationToken);

        metrics.ReminderSent();
    }

    private sealed record ReminderRow(
        Guid Id,
        Guid OrganizationId,
        Guid BranchId,
        Guid CategoryId,
        Guid AssignedToId,
        string Title,
        DateTimeOffset CurrentDueAt);
}
