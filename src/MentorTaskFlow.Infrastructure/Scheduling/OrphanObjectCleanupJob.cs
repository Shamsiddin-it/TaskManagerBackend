using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Hangfire;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Scheduling;

/// <summary>
/// Removes objects left in storage without a submission row (TZ 17.7).
/// </summary>
/// <remarks>
/// An upload writes the object first and the row second, so a failure between the two leaves an
/// orphan. That order is deliberate: the reverse would leave a row pointing at nothing, which no
/// cleanup can repair (<c>SUB-030</c>, <c>SUB-032</c>).
/// </remarks>
public sealed class OrphanObjectCleanupJob(
    MentorTaskFlowDbContext dbContext,
    IAmazonS3 client,
    IOutboxWriter outboxWriter,
    IAuditWriter auditWriter,
    IOptions<StorageOptions> options,
    ILogger<OrphanObjectCleanupJob> logger,
    IClock clock)
{
    private readonly StorageOptions _options = options.Value;

    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddHours(-_options.OrphanTtlHours);

        // TEN-067: one scope prefix per pass, never a batch mixing branches. An incident review of a
        // deletion should be able to say which branch it touched without reading every key in it.
        var prefixes = await dbContext.Branches
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(b => new { b.OrganizationId, BranchId = b.Id })
            .ToListAsync(cancellationToken);

        foreach (var scope in prefixes)
        {
            await SweepPrefixAsync(scope.OrganizationId, scope.BranchId, cutoff, cancellationToken);
        }
    }

    private async Task SweepPrefixAsync(
        Guid organizationId,
        Guid branchId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var prefix = $"submissions/{organizationId}/{branchId}/";
        var request = new ListObjectsV2Request { BucketName = _options.Bucket, Prefix = prefix };

        ListObjectsV2Response page;
        var deleted = 0;

        do
        {
            page = await client.ListObjectsV2Async(request, cancellationToken);

            foreach (var item in page.S3Objects ?? [])
            {
                // Younger than the grace period: an upload may still be committing its row.
                if (item.LastModified > cutoff.UtcDateTime)
                {
                    continue;
                }

                if (await IsOrphanAsync(item.Key, organizationId, branchId, cancellationToken))
                {
                    await client.DeleteObjectAsync(_options.Bucket, item.Key, cancellationToken);
                    deleted++;
                }
            }

            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated ?? false);

        if (deleted > 0)
        {
            auditWriter.WriteSystem(
                new AuditEntry
                {
                    Action = AuditActions.StorageOrphanCleanup,
                    EntityType = "StorageObject",
                    Metadata = JsonSerializer.SerializeToDocument(new { deleted, prefix }),
                },
                organizationId,
                branchId);

            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Removed {Count} orphan object(s) under one branch prefix.", deleted);
        }
    }

    /// <summary>
    /// Decides whether an object may be deleted.
    /// </summary>
    /// <remarks>
    /// <c>TEN-068</c>: a key whose prefix disagrees with the scope of its submission row is <b>not</b>
    /// deleted. A scope mismatch is evidence of a defect or of interference, and destroying the
    /// evidence is the one response that cannot be undone — so it raises a security alert instead.
    /// </remarks>
    private async Task<bool> IsOrphanAsync(
        string key,
        Guid organizationId,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        var submission = await dbContext.Submissions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.StorageKey == key)
            .Select(s => new { s.Id, s.OrganizationId, s.BranchId })
            .FirstOrDefaultAsync(cancellationToken);

        if (submission is null)
        {
            return true;
        }

        if (submission.OrganizationId == organizationId && submission.BranchId == branchId)
        {
            return false;
        }

        await RaiseScopeInconsistencyAsync(key, organizationId, branchId, cancellationToken);

        return false;
    }

    private async Task RaiseScopeInconsistencyAsync(
        string key,
        Guid organizationId,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        logger.LogError("Storage object {Key} disagrees with the scope of its submission row.", key);

        auditWriter.WriteSystem(
            new AuditEntry
            {
                Action = AuditActions.StorageCrossScopeInconsistency,
                EntityType = "StorageObject",
                Result = AuditResult.Failure,
                FailureReason = "scope_mismatch",
                Metadata = JsonSerializer.SerializeToDocument(new { key }),
            },
            organizationId,

            // Organization-level: the inconsistency is about which branch the object belongs to, so
            // attributing the alert to one of the candidates would beg the question (TEN-042).
            branchId: null);

        var admins = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.OrganizationId == organizationId
                        && u.Role == UserRole.Admin
                        && u.AdminScope == AdminScope.Organization
                        && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        foreach (var adminId in admins)
        {
            await outboxWriter.EnqueueSystemAsync(
                new OutboxEntry
                {
                    RecipientUserId = adminId,
                    EventType = NotificationEventTypes.OrganizationSystemAlert,
                    EntityId = branchId,
                    Discriminator = $"storage-scope:{adminId:N}",
                    Payload = JsonSerializer.SerializeToDocument(new { reason = "storage_scope_mismatch" }),
                    IsSystemAlert = true,
                },
                organizationId,
                branchId: null,
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
