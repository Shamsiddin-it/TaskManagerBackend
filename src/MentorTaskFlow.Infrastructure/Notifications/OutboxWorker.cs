using MentorTaskFlow.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Notifications;

/// <summary>
/// Drives the outbox: a delivery pass every <c>PollSeconds</c> and a lease sweep every
/// <c>LeaseSweepSeconds</c> (TZ 18.4).
/// </summary>
/// <remarks>
/// <para>
/// A hosted service rather than a scheduled job. The outbox is a continuously drained queue, not
/// something that happens at a time of day; Hangfire arrives with the scheduler of Phase 14 for the
/// jobs that genuinely have a schedule.
/// </para>
/// <para>
/// Enabled by configuration and off by default, so only <c>mtf-worker</c> drains the queue.
/// Several API replicas each running a loop would not corrupt anything — <c>SKIP LOCKED</c> sees to
/// that — but they would multiply the load on the mail provider for no benefit (<c>DEPLOY-013</c>).
/// </para>
/// </remarks>
public sealed class OutboxWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationOptions> options,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    private readonly NotificationOptions _options = options.Value;

    /// <summary>Identifies the holder of a lease in <c>locked_by</c>, for diagnosing a stuck worker.</summary>
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableWorker)
        {
            logger.LogInformation("Outbox worker is disabled in this process.");
            return;
        }

        logger.LogInformation("Outbox worker {WorkerId} started.", _workerId);

        var sweep = SweepLeasesAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollSeconds));

        do
        {
            await RunSafelyAsync(
                (dispatcher, token) => dispatcher.DispatchAsync(_workerId, token),
                "delivery",
                stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        await sweep;
    }

    private async Task SweepLeasesAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.LeaseSweepSeconds));

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            await RunSafelyAsync(
                (dispatcher, token) => dispatcher.RecoverExpiredLeasesAsync(token),
                "lease recovery",
                stoppingToken);
        }
    }

    /// <summary>
    /// Runs one pass in its own scope, and never lets a failure stop the loop.
    /// </summary>
    /// <remarks>
    /// An unhandled exception in a <c>BackgroundService</c> stops the host by default. A transient
    /// database error must not take the process down and must not silently end delivery for the rest
    /// of its life — so every pass is isolated and logged.
    /// </remarks>
    private async Task RunSafelyAsync(
        Func<OutboxDispatcher, CancellationToken, Task<int>> pass,
        string description,
        CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();

            await pass(dispatcher, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a fault.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Outbox {Description} pass failed; the loop continues.", description);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
