using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Notifications;

/// <summary>
/// One pass of the outbox: claim a batch, deliver it, record what happened (TZ 18.4).
/// </summary>
/// <remarks>
/// Separated from the hosted service so the same pass can be driven by a test, by a manual trigger or
/// by a scheduler without a running loop.
/// </remarks>
public sealed class OutboxDispatcher(
    MentorTaskFlowDbContext dbContext,
    IEnumerable<INotificationSender> senders,
    NotificationMetrics metrics,
    IOptions<NotificationOptions> options,
    ILogger<OutboxDispatcher> logger,
    IClock clock)
{
    private readonly NotificationOptions _options = options.Value;

    /// <summary>Delivers one batch and returns how many rows were attempted.</summary>
    public async Task<int> DispatchAsync(string workerId, CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(workerId, cancellationToken);

        if (claimed.Count == 0)
        {
            return 0;
        }

        var recipients = await LoadRecipientsAsync(claimed, cancellationToken);

        foreach (var row in claimed)
        {
            await DeliverAsync(row, recipients, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await RaiseDeadLetterAlertsAsync(claimed, cancellationToken);

        return claimed.Count;
    }

    /// <summary>
    /// Claims a batch with <c>FOR UPDATE SKIP LOCKED</c> (<c>NTF-011</c>).
    /// </summary>
    /// <remarks>
    /// The skip is what lets several workers share one queue without a global lock: a row another
    /// worker holds is passed over rather than waited on, so throughput scales with workers instead of
    /// serialising on the head of the queue.
    /// </remarks>
    private async Task<List<NotificationOutbox>> ClaimAsync(string workerId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var ids = await dbContext.Database
            .SqlQuery<Guid>($"""
                UPDATE notification_outbox
                SET status = 'Processing',
                    locked_at = {now},
                    locked_by = {workerId},
                    attempts = attempts + 1,
                    last_attempt_at = {now}
                WHERE id IN (
                    SELECT id FROM notification_outbox
                    WHERE status = 'Pending' AND next_attempt_at <= {now}
                    ORDER BY next_attempt_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT {_options.BatchSize}
                )
                RETURNING id
                """)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return [];
        }

        // Read back through the model, with the tenant filter suppressed: the worker serves every
        // organization and has no request scope of its own.
        return await dbContext.NotificationOutbox
            .IgnoreQueryFilters()
            .Where(n => ids.Contains(n.Id))
            .ToListAsync(cancellationToken);
    }

    private async Task DeliverAsync(
        NotificationOutbox row,
        IReadOnlyDictionary<Guid, Recipient> recipients,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var channel = row.Channel.ToString();

        if (!recipients.TryGetValue(row.UserId, out var recipient))
        {
            row.SendToDeadLetter("Получатель не найден.", now);
            metrics.DeadLettered(channel, "recipient_missing");
            return;
        }

        var sender = senders.FirstOrDefault(s => s.Channel == row.Channel);

        if (sender is null)
        {
            // The Telegram channel arrives in the next phase. Until then its rows wait rather than
            // dead-letter: NTF-001 already keeps them from being created without a binding, and
            // discarding them would lose notifications the moment the channel ships.
            row.RescheduleOrDeadLetter($"Канал {channel} пока не поддерживается.", now);
            return;
        }

        var result = await sender.SendAsync(
            new NotificationMessage(
                row.EventType,
                recipient.FullName,
                recipient.Email,
                recipient.TelegramChatId,
                row.Payload),
            cancellationToken);

        if (result.Succeeded)
        {
            row.MarkSent(result.ProviderMessageId, now);
            metrics.Sent(channel);
            return;
        }

        if (result.Failure is DeliveryFailure.Permanent)
        {
            row.SendToDeadLetter(result.Error ?? "Постоянная ошибка доставки.", now);
            metrics.DeadLettered(channel, "permanent");

            // OBS: Error level, because NTF-023 makes the log one of the three channels through which
            // a dead letter becomes known without depending on the mail provider.
            logger.LogError(
                "Notification {EventType} dead-lettered permanently on {Channel}.",
                row.EventType,
                channel);

            return;
        }

        if (row.RescheduleOrDeadLetter(result.Error ?? "Временная ошибка доставки.", now))
        {
            metrics.Retried(channel);
        }
        else
        {
            metrics.DeadLettered(channel, "attempts_exhausted");
            logger.LogError(
                "Notification {EventType} dead-lettered after {Attempts} attempts on {Channel}.",
                row.EventType,
                row.Attempts,
                channel);
        }
    }

    /// <summary>
    /// Returns rows whose lease has expired to the queue (<c>NTF-012</c>).
    /// </summary>
    /// <remarks>
    /// Without this a process killed mid-send leaves its rows in <c>Processing</c> for ever — the one
    /// state from which nothing recovers them, because the claim query only looks at <c>Pending</c>.
    /// </remarks>
    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var cutoff = now - NotificationOutbox.LeaseDuration;

        var stranded = await dbContext.NotificationOutbox
            .IgnoreQueryFilters()
            .Where(n => n.Status == NotificationStatus.Processing && n.LockedAt < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var row in stranded)
        {
            row.ReleaseExpiredLease(now);
        }

        if (stranded.Count > 0)
        {
            logger.LogWarning("Recovered {Count} notification(s) from an expired worker lease.", stranded.Count);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return stranded.Count;
    }

    /// <summary>
    /// Raises the hourly digest about dead letters (<c>NTF-022</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recursion guard of <c>NTF-021</c> is the first line here: a row that is itself a system
    /// alert produces no alert of its own. Without it an unreachable mail provider would generate an
    /// alert about the failed alert, and then an alert about that, without end.
    /// </para>
    /// <para>
    /// One digest per hour per organization, deduplicated by the hour stamp, and it carries a count
    /// rather than a message per failure — a hundred dead letters must not become a hundred emails
    /// through the provider that is already failing.
    /// </para>
    /// </remarks>
    private async Task RaiseDeadLetterAlertsAsync(
        IReadOnlyCollection<NotificationOutbox> processed,
        CancellationToken cancellationToken)
    {
        var failures = processed
            .Where(n => n.Status is NotificationStatus.DeadLetter && !n.IsSystemAlert)
            .GroupBy(n => n.OrganizationId)
            .ToList();

        if (failures.Count == 0)
        {
            return;
        }

        var now = clock.UtcNow;
        var hour = now.UtcDateTime.ToString("yyyy-MM-dd-HH");

        foreach (var group in failures)
        {
            var key = DeduplicationKey.BuildSystem(
                group.Key,
                NotificationEventTypes.NotificationDeadLetter,
                NotificationChannel.Email,
                $"deadletter-alert:{hour}");

            var admins = await dbContext.Users
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.OrganizationId == group.Key
                            && u.Role == UserRole.Admin
                            && u.AdminScope == AdminScope.Organization
                            && u.IsActive)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

            foreach (var adminId in admins)
            {
                // Each administrator needs their own row, so the hourly window is checked per
                // administrator — against the key that is actually written, not the base one.
                var adminKey = $"{key}:{adminId:N}";

                if (await dbContext.NotificationOutbox
                        .IgnoreQueryFilters()
                        .AnyAsync(n => n.DeduplicationKey == adminKey, cancellationToken))
                {
                    continue;
                }

                dbContext.NotificationOutbox.Add(NotificationOutbox.Enqueue(
                    adminId,
                    group.Key,

                    // Organization-level: the failure is not a fact of any one branch (TEN-042).
                    branchId: null,
                    categoryId: null,
                    NotificationChannel.Email,
                    NotificationEventTypes.NotificationDeadLetter,
                    JsonSerializer.SerializeToDocument(new { failedCount = group.Count() }),
                    adminKey,
                    now,
                    isSystemAlert: true));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<Guid, Recipient>> LoadRecipientsAsync(
        IReadOnlyCollection<NotificationOutbox> rows,
        CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.UserId).Distinct().ToArray();

        return await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new Recipient(u.Id, u.FullName, u.Email, u.TelegramChatId))
            .ToDictionaryAsync(r => r.Id, cancellationToken);
    }

    private sealed record Recipient(Guid Id, string FullName, string? Email, string? TelegramChatId);
}
