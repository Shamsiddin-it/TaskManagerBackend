using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Notifications;

/// <inheritdoc />
public sealed class OutboxWriter(
    MentorTaskFlowDbContext dbContext,
    IBranchContext branchContext,
    NotificationMetrics metrics,
    IClock clock) : IOutboxWriter
{
    public Task EnqueueAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        var branchId = NotificationEventTypes.OrganizationLevelEvents.Contains(entry.EventType)
            ? null
            : branchContext.EffectiveBranchId;

        return AddAsync(entry, branchContext.EffectiveOrganizationId, branchId, cancellationToken);
    }

    public Task EnqueueSystemAsync(
        OutboxEntry entry,
        Guid organizationId,
        Guid? branchId,
        CancellationToken cancellationToken) =>
        AddAsync(entry, organizationId, branchId, cancellationToken);

    /// <summary>
    /// Expands one logical event into the rows its channel policy calls for (18.1).
    /// </summary>
    /// <remarks>
    /// <c>TelegramPreferred</c> is the case worth spelling out: a Telegram row is written when the
    /// recipient has a binding, and an email row when they do not. The absence of a binding is not an
    /// error and not a dead letter — it is counted and the message goes by mail instead, which is
    /// exactly what keeps a person who never connected Telegram from silently receiving nothing
    /// (<c>NTF-001</c>, <c>NTF-002</c>).
    /// </remarks>
    private async Task AddAsync(
        OutboxEntry entry,
        Guid organizationId,
        Guid? branchId,
        CancellationToken cancellationToken)
    {
        var policy = ChannelPolicies.For(entry.EventType);
        var now = clock.UtcNow;

        var telegramBound = policy is not ChannelPolicy.EmailOnly
                            && await HasTelegramAsync(entry.RecipientUserId, cancellationToken);

        foreach (var channel in ResolveChannels(policy, telegramBound, entry.EventType))
        {
            var key = DeduplicationKey.Build(
                organizationId,
                branchId,
                entry.EventType,
                entry.EntityId,
                channel,
                entry.Discriminator);

            // The duplicate is skipped rather than allowed to fail: a repeated notification must not
            // roll back the business event it travels with (NTF-009). The unique index remains the
            // final guard for the race this check cannot close.
            if (await ExistsAsync(key, cancellationToken))
            {
                continue;
            }

            dbContext.NotificationOutbox.Add(NotificationOutbox.Enqueue(
                entry.RecipientUserId,
                organizationId,
                branchId,
                entry.CategoryId,
                channel,
                entry.EventType,
                entry.Payload,
                key,
                now,
                entry.IsSystemAlert));
        }
    }

    /// <summary>
    /// Checks both the database and the rows already staged in this unit of work.
    /// </summary>
    /// <remarks>
    /// The change tracker matters as much as the table: two enqueues in one transaction — the same
    /// event raised for a recipient twice — would otherwise both pass the database check and collide
    /// at commit.
    /// </remarks>
    private async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        var staged = dbContext.ChangeTracker
            .Entries<NotificationOutbox>()
            .Any(e => e.State is EntityState.Added && e.Entity.DeduplicationKey == key);

        return staged || await dbContext.NotificationOutbox
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(n => n.DeduplicationKey == key, cancellationToken);
    }

    private IEnumerable<NotificationChannel> ResolveChannels(
        ChannelPolicy policy,
        bool telegramBound,
        string eventType)
    {
        switch (policy)
        {
            case ChannelPolicy.EmailOnly:
                yield return NotificationChannel.Email;
                break;

            case ChannelPolicy.TelegramPreferred:
                if (telegramBound)
                {
                    yield return NotificationChannel.Telegram;
                }
                else
                {
                    metrics.SkippedTelegram(eventType);
                    yield return NotificationChannel.Email;
                }

                break;

            case ChannelPolicy.Both:
                yield return NotificationChannel.Email;

                // NTF-001: no Telegram row without a binding. Writing one would guarantee a failure
                // that is neither the sender's fault nor worth retrying.
                if (telegramBound)
                {
                    yield return NotificationChannel.Telegram;
                }
                else
                {
                    metrics.SkippedTelegram(eventType);
                }

                break;
        }
    }

    private Task<bool> HasTelegramAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.TelegramChatId != null, cancellationToken);
}
