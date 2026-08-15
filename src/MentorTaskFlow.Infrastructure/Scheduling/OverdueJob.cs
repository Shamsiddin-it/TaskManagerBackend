using System.Text.Json;
using Hangfire;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Scheduling;

/// <summary>
/// Moves assignments past their deadline to <c>Overdue</c> (TZ 20.6).
/// </summary>
/// <remarks>
/// Only <c>Assigned</c> and <c>NeedsRework</c> qualify. Submitted work is already with the Lead, and
/// a Lead who is slow to review is measured by <c>FirstReviewResponseTime</c> rather than by moving
/// the mentor's task to a status that blames them (14.4).
/// </remarks>
public sealed class OverdueJob(
    MentorTaskFlowDbContext dbContext,
    IOutboxWriter outboxWriter,
    SchedulerMetrics metrics,
    IOptions<SchedulerOptions> options,
    ILogger<OverdueJob> logger,
    IClock clock)
{
    private readonly SchedulerOptions _options = options.Value;

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await ClaimBatchAsync(now, cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            total += batch.Count;

            // SCH-019: batches in separate transactions, so a long-running pass never holds locks
            // across the user-facing operations happening at the same time.
            await RecordBatchAsync(batch, now, cancellationToken);
        }

        if (total > 0)
        {
            metrics.MarkedOverdue(total);
            logger.LogInformation("Marked {Count} assignment(s) overdue.", total);
        }
    }

    /// <summary>
    /// Moves one batch with a conditional UPDATE and returns what actually changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WHERE status = @expected</c> is what makes the pass idempotent: a concurrent run — or a
    /// mentor submitting in the same instant — leaves the row already moved, the update touches zero
    /// rows, and the loser produces no event rather than a duplicate one (<c>SCH-007</c>).
    /// </para>
    /// <para>
    /// <c>COALESCE</c> keeps the <b>first</b> overdue moment. A task returned for rework can go overdue
    /// again, and <c>OverdueAt</c> answers «when did this first slip», which the second pass must not
    /// overwrite (14.4).
    /// </para>
    /// <para>
    /// <c>TEN-052</c>: the selection is bounded by active organizations and active branches. Work in a
    /// deactivated branch is not moved — nobody there can act on it, and marking it overdue would
    /// manufacture a metric about a branch that is closed (<c>BRN-033</c>).
    /// </para>
    /// </remarks>
    /// <remarks>
    /// The aliases are snake_case because the context applies <c>UseSnakeCaseNamingConvention</c> to
    /// query types as well as to entities, so EF looks for <c>assigned_to_id</c>, not <c>AssignedToId</c>.
    /// </remarks>
    private Task<List<OverdueRow>> ClaimBatchAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database
            .SqlQuery<OverdueRow>($"""
                WITH due AS (
                    SELECT a.id, a.status
                    FROM assignments a
                    JOIN branches b ON b.id = a.branch_id AND b.is_active
                    JOIN organizations o ON o.id = a.organization_id AND o.is_active
                    WHERE a.status IN ('Assigned', 'NeedsRework')
                      AND a.current_due_at < {now}
                    ORDER BY a.current_due_at
                    LIMIT {_options.OverdueBatchSize}
                )
                UPDATE assignments a
                SET status = 'Overdue',
                    overdue_at = COALESCE(a.overdue_at, {now}),
                    updated_at = {now},
                    last_event_sequence = a.last_event_sequence + 1
                FROM due
                WHERE a.id = due.id AND a.status = due.status
                RETURNING a.id,
                          due.status AS previous_status,
                          a.organization_id,
                          a.branch_id,
                          a.category_id,
                          a.assigned_to_id,
                          a.title,
                          a.current_due_at,
                          a.last_event_sequence AS sequence_number
                """)
            .ToListAsync(cancellationToken);

    /// <summary>Writes the event and the notifications for one batch (<c>SCH-019</c>).</summary>
    private async Task RecordBatchAsync(
        IReadOnlyCollection<OverdueRow> batch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var row in batch)
        {
            dbContext.TaskEvents.Add(TaskEvent.RecordSystem(
                row.Id,
                row.OrganizationId,
                row.BranchId,
                row.CategoryId,
                row.SequenceNumber,
                TaskEventType.MarkedOverdue,
                Enum.Parse<AssignmentStatus>(row.PreviousStatus),
                AssignmentStatus.Overdue,
                Guid.CreateVersion7(),
                now));

            await NotifyAsync(row, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    /// <summary>
    /// Tells the mentor and their Lead (Приложение E).
    /// </summary>
    /// <remarks>
    /// <c>NTF-016</c>: the key carries the deadline value, so a second slip against a <b>new</b>
    /// deadline after rework produces a new notification, while a repeated pass against the same one
    /// does not.
    /// </remarks>
    private async Task NotifyAsync(OverdueRow row, CancellationToken cancellationToken)
    {
        var leadId = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.CategoryId == row.CategoryId && u.Role == UserRole.Lead && u.IsActive)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var recipients = leadId is { } lead ? new[] { row.AssignedToId, lead } : [row.AssignedToId];

        foreach (var recipientId in recipients)
        {
            await outboxWriter.EnqueueSystemAsync(
                new OutboxEntry
                {
                    RecipientUserId = recipientId,
                    EventType = NotificationEventTypes.AssignmentOverdue,
                    EntityId = row.Id,
                    Discriminator = $"{row.CurrentDueAt:O}:{recipientId:N}",
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
        }
    }

    private sealed record OverdueRow(
        Guid Id,
        string PreviousStatus,
        Guid OrganizationId,
        Guid BranchId,
        Guid CategoryId,
        Guid AssignedToId,
        string Title,
        DateTimeOffset CurrentDueAt,
        int SequenceNumber);
}
