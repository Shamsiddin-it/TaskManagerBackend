using System.Text.Json;
using Hangfire;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MentorTaskFlow.Infrastructure.Scheduling;

/// <summary>
/// Removes expired tokens and delivered notifications (TZ 27.5, <c>AUD-010</c>).
/// </summary>
/// <remarks>
/// <para>
/// The one job of 20.8 that runs across the whole installation. It is allowed to, and only because it
/// selects <b>purely by time</b>: no business predicate, no branch, no category. A retention pass that
/// filtered on anything tenant-shaped would be deleting one organization's data on another's
/// authority (<c>TEN-052</c>).
/// </para>
/// <para>
/// Nothing that constitutes evidence is touched. Audit records, task events, submissions and reviews
/// have no retention here: they are what a dispute is settled with, and files in particular are kept
/// for as long as their assignment exists (27.5, <c>SUB-021</c>).
/// </para>
/// </remarks>
public sealed class RetentionJob(
    MentorTaskFlowDbContext dbContext,
    IAuditWriter auditWriter,
    ILogger<RetentionJob> logger,
    IClock clock)
{
    /// <summary>Used and expired security tokens: 30 days (27.5).</summary>
    private static readonly TimeSpan TokenRetention = TimeSpan.FromDays(30);

    /// <summary>Delivered notifications: 90 days. Dead letters are kept — they are unresolved.</summary>
    private static readonly TimeSpan SentNotificationRetention = TimeSpan.FromDays(90);

    /// <summary>
    /// AI summaries: 180 days (27.5, <c>AI-012</c>).
    /// </summary>
    /// <remarks>
    /// Deletable, unlike everything else this job leaves alone, because a summary is a cache and not
    /// evidence: the metrics it describes are still there, and asking for it again regenerates it.
    /// </remarks>
    private static readonly TimeSpan AiSummaryRetention = TimeSpan.FromDays(180);

    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var tokenCutoff = now - TokenRetention;
        var notificationCutoff = now - SentNotificationRetention;

        var securityTokens = await dbContext.UserSecurityTokens
            .Where(t => t.ExpiresAt < tokenCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var bindTokens = await dbContext.TelegramBindTokens
            .Where(t => t.ExpiresAt < tokenCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var refreshTokens = await dbContext.RefreshTokens
            .Where(t => t.ExpiresAt < tokenCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        // Only Sent. A DeadLetter row is an unresolved problem and its own alert channel (NTF-023);
        // deleting it on a timer would erase the record of a failure nobody has looked at yet.
        var notifications = await dbContext.NotificationOutbox
            .Where(n => n.Status == NotificationStatus.Sent && n.SentAt < notificationCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var aiSummaries = await dbContext.AiSummaries
            .IgnoreQueryFilters()
            .Where(s => s.CreatedAt < now - AiSummaryRetention)
            .ExecuteDeleteAsync(cancellationToken);

        var removed = securityTokens + bindTokens + refreshTokens + notifications + aiSummaries;

        if (removed == 0)
        {
            return;
        }

        logger.LogInformation("Retention removed {Count} expired row(s).", removed);

        await RecordAsync(securityTokens, bindTokens, refreshTokens, notifications, aiSummaries, cancellationToken);
    }

    /// <summary>
    /// One audit record per organization, carrying the counts (<c>TEN-069</c>).
    /// </summary>
    /// <remarks>
    /// <c>AuditLog.OrganizationId</c> is mandatory always, and a pass that spans the installation has
    /// no single one to name — so the totals are attributed to each organization that exists rather
    /// than to none.
    /// </remarks>
    private async Task RecordAsync(
        int securityTokens,
        int bindTokens,
        int refreshTokens,
        int notifications,
        int aiSummaries,
        CancellationToken cancellationToken)
    {
        var organizations = await dbContext.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        foreach (var organizationId in organizations)
        {
            auditWriter.WriteSystem(
                new AuditEntry
                {
                    Action = AuditActions.RetentionCleanup,
                    EntityType = "Retention",
                    Metadata = JsonSerializer.SerializeToDocument(new
                    {
                        securityTokens,
                        bindTokens,
                        refreshTokens,
                        notifications,
                        aiSummaries,
                    }),
                },
                organizationId,

                // Organization-level: the pass belongs to no branch (TEN-048).
                branchId: null);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
