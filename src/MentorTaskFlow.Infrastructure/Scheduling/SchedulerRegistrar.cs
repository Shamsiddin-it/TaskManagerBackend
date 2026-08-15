using Hangfire;
using Hangfire.Storage;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Scheduling;

/// <summary>
/// Registers the recurring jobs of TZ 20.1 at startup.
/// </summary>
/// <remarks>
/// <para>
/// Auto-generation gets one job per distinct <c>CategorySettings.TimeZoneId</c> in the whole
/// installation (<c>SCH-001</c>, <c>TEN-055</c>). The schedule belongs to the category, not the
/// branch: <c>Branch.TimeZoneId</c> is only the default for new categories and produces no job of its
/// own.
/// </para>
/// <para>
/// The set is recomputed on every start, and stale zone jobs are removed — a category whose zone
/// changed would otherwise keep generating at the old local hour for ever.
/// </para>
/// </remarks>
public sealed class SchedulerRegistrar(
    IServiceScopeFactory scopeFactory,
    IRecurringJobManager recurringJobs,
    IOptions<SchedulerOptions> options,
    ILogger<SchedulerRegistrar> logger) : IHostedService
{
    private const string AutoGenerationPrefix = "auto-generation:";

    public const string OverdueJobId = "overdue-scan";
    public const string ReminderJobId = "deadline-reminders";
    public const string OrphanCleanupJobId = "storage-orphan-cleanup";
    public const string RetentionJobId = "retention-cleanup";

    private readonly SchedulerOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Scheduler is disabled; no recurring job registered.");
            return;
        }

        RegisterFixedJobs();
        await RegisterAutoGenerationAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The jobs of 20.1 whose schedule is in UTC and does not depend on any category.</summary>
    private void RegisterFixedJobs()
    {
        var every = $"*/{_options.OverdueIntervalMinutes} * * * *";

        recurringJobs.AddOrUpdate<OverdueJob>(
            OverdueJobId,
            job => job.RunAsync(CancellationToken.None),
            every,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobs.AddOrUpdate<DeadlineReminderJob>(
            ReminderJobId,
            job => job.RunAsync(CancellationToken.None),
            every,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobs.AddOrUpdate<OrphanObjectCleanupJob>(
            OrphanCleanupJobId,
            job => job.RunAsync(CancellationToken.None),
            "0 3 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobs.AddOrUpdate<RetentionJob>(
            RetentionJobId,
            job => job.RunAsync(CancellationToken.None),
            "0 4 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>One job per distinct category time zone (<c>SCH-001</c>).</summary>
    private async Task RegisterAutoGenerationAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MentorTaskFlowDbContext>();

        var zones = await dbContext.CategorySettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(s => s.TimeZoneId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var wanted = new HashSet<string>(zones.Select(z => AutoGenerationPrefix + z), StringComparer.Ordinal);

        foreach (var timeZoneId in zones)
        {
            if (!TryFindZone(timeZoneId, out var zone))
            {
                // A zone the host's tzdata does not know: registering it would throw at every trigger.
                // The category is unusable until the deployment is fixed, and the log says which.
                logger.LogError("Unknown time zone {TimeZone}; auto-generation is not scheduled for it.", timeZoneId);
                continue;
            }

            recurringJobs.AddOrUpdate<AutoGenerationJob>(
                AutoGenerationPrefix + timeZoneId,
                job => job.RunAsync(timeZoneId, CancellationToken.None),
                $"0 {_options.AutoGenerationHour} * * *",
                new RecurringJobOptions { TimeZone = zone });
        }

        RemoveStaleZoneJobs(wanted);
    }

    /// <summary>
    /// Drops auto-generation jobs for zones no category uses any more.
    /// </summary>
    /// <remarks>
    /// Without this, changing a category's zone would leave the old job registered and generating at
    /// the previous local hour indefinitely — the kind of duplicate that looks like a correct schedule
    /// until someone compares two mornings.
    /// </remarks>
    private void RemoveStaleZoneJobs(IReadOnlySet<string> wanted)
    {
        using var connection = JobStorage.Current.GetConnection();

        var stale = connection.GetRecurringJobs()
            .Where(j => j.Id.StartsWith(AutoGenerationPrefix, StringComparison.Ordinal) && !wanted.Contains(j.Id))
            .ToList();

        foreach (var job in stale)
        {
            logger.LogInformation("Removing stale auto-generation job {JobId}.", job.Id);
            recurringJobs.RemoveIfExists(job.Id);
        }
    }

    private static bool TryFindZone(string timeZoneId, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
