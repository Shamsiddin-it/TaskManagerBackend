using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MentorTaskFlow.Infrastructure.Observability;

/// <summary>One row of the composition snapshot the gauges of <c>TEN-096</c> report.</summary>
public sealed record UserCount(string Organization, string Branch, string Role, string AdminScope, long Count);

/// <summary>One organization's branch counts.</summary>
public sealed record BranchCount(string Organization, long Active, long Inactive);

/// <summary>
/// The composition of the installation, refreshed on a timer and read at scrape time.
/// </summary>
/// <remarks>
/// Observable gauges are polled by the exporter, and the callback must return immediately: querying
/// the database inside it would block the scrape and, on a slow database, time the collector out
/// exactly when the collector is most needed. A snapshot refreshed in the background costs one cheap
/// aggregate query a minute and makes the scrape free.
/// </remarks>
public sealed class TenantGaugeSnapshot
{
    private volatile IReadOnlyList<BranchCount> _branches = [];
    private volatile IReadOnlyList<UserCount> _users = [];

    public IReadOnlyList<BranchCount> Branches => _branches;

    public IReadOnlyList<UserCount> Users => _users;

    public void Update(IReadOnlyList<BranchCount> branches, IReadOnlyList<UserCount> users)
    {
        _branches = branches;
        _users = users;
    }
}

/// <summary>
/// Keeps <see cref="TenantGaugeSnapshot"/> current (<c>TEN-096</c>).
/// </summary>
/// <remarks>
/// Runs in every process, worker and API alike. The counts describe the installation rather than the
/// process, so whichever replica is scraped reports the same figures — which is what makes
/// <c>users_total</c> summable across an alert rule rather than dependent on which pod answered.
/// </remarks>
public sealed class TenantGaugeRefresher(
    IServiceScopeFactory scopeFactory,
    TenantGaugeSnapshot snapshot,
    ILogger<TenantGaugeRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            await RefreshAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<MentorTaskFlowDbContext>();

            // IgnoreQueryFilters throughout: this is an installation-wide count with no request scope
            // behind it, and it selects nothing but aggregates — no row of any tenant leaves here.
            var branches = await context.Branches
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Join(
                    context.Organizations.IgnoreQueryFilters().AsNoTracking(),
                    b => b.OrganizationId,
                    o => o.Id,
                    (b, o) => new { o.Slug, b.IsActive })
                .GroupBy(x => x.Slug)
                .Select(g => new BranchCount(
                    g.Key,
                    g.Count(x => x.IsActive),
                    g.Count(x => !x.IsActive)))
                .ToListAsync(cancellationToken);

            var users = await context.Users
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u => u.IsActive)
                .Join(
                    context.Organizations.IgnoreQueryFilters().AsNoTracking(),
                    u => u.OrganizationId,
                    o => o.Id,
                    (u, o) => new { User = u, o.Slug })
                .GroupBy(x => new
                {
                    x.Slug,

                    // An Organization Admin has no branch; the label says so rather than being absent,
                    // so a query summing over branches does not silently drop them.
                    Branch = x.User.BranchId,
                    x.User.Role,
                    x.User.AdminScope,
                })
                .Select(g => new
                {
                    g.Key.Slug,
                    g.Key.Branch,
                    Role = g.Key.Role.ToString(),
                    AdminScope = g.Key.AdminScope == null ? "none" : g.Key.AdminScope.ToString()!,
                    Count = g.LongCount(),
                })
                .ToListAsync(cancellationToken);

            var codes = await context.Branches
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToDictionaryAsync(b => b.Id, b => b.Code, cancellationToken);

            snapshot.Update(
                branches,
                [.. users.Select(u => new UserCount(
                    u.Slug,
                    u.Branch is { } id ? codes.GetValueOrDefault(id, TenantLabelResolver.Unknown) : "none",
                    u.Role,
                    u.AdminScope,
                    u.Count))]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A stale gauge is better than a crashed process: the counts are operational information,
            // and the database being unreachable is already an Unhealthy readiness probe.
            logger.LogWarning(exception, "Tenant gauges could not be refreshed; the previous snapshot stands.");
        }
    }
}

/// <summary>
/// The gauges of <c>TEN-096</c>, read from the snapshot.
/// </summary>
/// <remarks>
/// Registered as a singleton and constructed at startup so the instruments exist before the first
/// scrape. A gauge that appears only after its first value has been observed produces gaps in a graph
/// that look like an outage.
/// </remarks>
public sealed class TenantGauges
{
    public const string MeterName = "MentorTaskFlow.Tenancy";

    private static readonly ConcurrentDictionary<string, byte> Registered = new();

    public TenantGauges(IMeterFactory meterFactory, TenantGaugeSnapshot snapshot)
    {
        var meter = meterFactory.Create(MeterName);

        meter.CreateObservableGauge(
            "active_branches_total",
            () => snapshot.Branches.Select(b => new Measurement<long>(
                b.Active,
                new KeyValuePair<string, object?>(MetricLabels.Organization, b.Organization))),
            description: "Active branches per organization (TEN-096).");

        meter.CreateObservableGauge(
            "inactive_branches_total",
            () => snapshot.Branches.Select(b => new Measurement<long>(
                b.Inactive,
                new KeyValuePair<string, object?>(MetricLabels.Organization, b.Organization))),
            description: "Deactivated branches per organization (TEN-096).");

        meter.CreateObservableGauge(
            "users_total",
            () => snapshot.Users.Select(u => new Measurement<long>(
                u.Count,
                new KeyValuePair<string, object?>(MetricLabels.Organization, u.Organization),
                new(MetricLabels.Branch, u.Branch),
                new(MetricLabels.Role, u.Role),
                new(MetricLabels.AdminScope, u.AdminScope))),
            description: "Active users by organization, branch, role and admin scope (TEN-096).");

        Registered.TryAdd(MeterName, 0);
    }
}
