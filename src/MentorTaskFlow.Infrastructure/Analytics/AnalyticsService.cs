using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Analytics;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Tenancy;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Analytics;

/// <inheritdoc />
public sealed class AnalyticsService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    MetricsQuery metricsQuery,
    IDeadlineCalculator deadlines,
    AnalyticsMetrics metrics,
    IClock clock) : IAnalyticsService
{
    /// <summary>The privacy threshold of <c>ANA-012</c>.</summary>
    private const int MinimumMentors = 5;

    public async Task<ReportDto> GetPersonalAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        var mentorId = ResolvePersonalSubject(actor, query);

        var scope = new ReportScope
        {
            OrganizationId = branchContext.EffectiveOrganizationId,

            // USER-017: ownership never crosses a branch, so a personal report is bounded by the
            // person's current branch — work done before a transfer is not in it either.
            BranchId = actor.BranchId ?? query.BranchId,
            MentorId = mentorId,

            // ANA-014: the personal report breaks down by category, because a mentor who changed
            // category has history in both and merging them would hide that.
            Grouping = ReportGrouping.BranchCategory,
        };

        return await BuildAsync(scope, query, cancellationToken);
    }

    public async Task<ReportDto> GetTeamAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        var scope = ResolveTeamScope(actor, query);

        // ANA-012 and TEN-072: checked inside the scope actually requested, before any figure is
        // computed. Returning a partial answer and refusing the rest would leak by subtraction.
        if (actor.Role is UserRole.Mentor)
        {
            await EnsureSampleSizeAsync(scope, cancellationToken);
        }

        return await BuildAsync(scope, query, cancellationToken);
    }

    public async Task<ReportDto> GetBranchComparisonAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        // TEN-070: comparing branches is the Organization Admin's report alone. A Branch Admin has one
        // branch, so the comparison has no meaning for them and no partial version is offered.
        if (actor is not { Role: UserRole.Admin, AdminScope: AdminScope.Organization })
        {
            throw new ForbiddenException(ErrorCodes.Forbidden, "Сравнение филиалов доступно только Organization Admin.");
        }

        var scope = new ReportScope
        {
            OrganizationId = branchContext.EffectiveOrganizationId,
            Grouping = ReportGrouping.Branch,
            IsCrossBranch = true,
        };

        return await BuildAsync(scope, query, cancellationToken);
    }

    // -----------------------------------------------------------------
    // Scope resolution (TEN-070)
    // -----------------------------------------------------------------

    /// <summary>
    /// Decides whose personal report is being asked for.
    /// </summary>
    /// <remarks>
    /// <c>ANA-013</c>: a Mentor naming anyone but themselves is a 400, not a silently narrowed result.
    /// The allowlist exists because a sequence of narrow queries is how an anonymised aggregate gets
    /// unpicked.
    /// </remarks>
    private static Guid ResolvePersonalSubject(ICurrentUserContext actor, ReportQuery query)
    {
        if (actor.Role is not UserRole.Mentor)
        {
            return query.MentorId
                ?? throw new ValidationAppException("mentorId", "Укажите ментора для персонального отчёта.");
        }

        if (query.MentorId is { } requested && requested != actor.UserId)
        {
            throw new ValidationAppException("mentorId", "Ментор видит только собственные показатели.");
        }

        return actor.UserId;
    }

    private ReportScope ResolveTeamScope(ICurrentUserContext actor, ReportQuery query)
    {
        var organizationId = branchContext.EffectiveOrganizationId;

        return actor switch
        {
            // The whole organization, optionally narrowed to one branch. All-branches is the only
            // place rows from several branches may be combined, and it is flagged (TEN-071).
            { Role: UserRole.Admin, AdminScope: AdminScope.Organization } => new ReportScope
            {
                OrganizationId = organizationId,
                BranchId = query.BranchId,
                CategoryId = query.CategoryId,
                Grouping = ReportGrouping.BranchCategory,
                IsCrossBranch = query.BranchId is null,
            },

            // Fixed to their own branch: a branchId in the query is ignored rather than honoured,
            // because it is not theirs to choose (TEN-070).
            { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => new ReportScope
            {
                OrganizationId = organizationId,
                BranchId = actor.BranchId,
                CategoryId = query.CategoryId,
                Grouping = ReportGrouping.BranchCategory,
            },

            // ANA-011: a Lead sees each mentor of their category by name, plus the team aggregate.
            { Role: UserRole.Lead } => new ReportScope
            {
                OrganizationId = organizationId,
                BranchId = actor.BranchId,
                CategoryId = actor.CategoryId,
                Grouping = ReportGrouping.Mentor,
            },

            // A Mentor gets the anonymised team figure and no breakdown at all.
            _ => new ReportScope
            {
                OrganizationId = organizationId,
                BranchId = actor.BranchId,
                CategoryId = actor.CategoryId,
                Grouping = ReportGrouping.None,
            },
        };
    }

    /// <summary>
    /// Refuses an anonymised report over fewer than five mentors (<c>ANA-012</c>, <c>TEN-072</c>).
    /// </summary>
    /// <remarks>
    /// Counted inside the requested scope, never across branches. If a category of one branch has
    /// three mentors, the answer is 403 even though the identically named category of another branch
    /// has four more — combining them to clear the threshold is exactly the workaround the rule
    /// forbids.
    /// </remarks>
    private async Task EnsureSampleSizeAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        var distinctMentors = await dbContext.Assignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.OrganizationId == scope.OrganizationId)
            .Where(a => scope.BranchId == null || a.BranchId == scope.BranchId)
            .Where(a => scope.CategoryId == null || a.CategoryId == scope.CategoryId)
            .Where(a => a.Status != AssignmentStatus.Draft && a.Status != AssignmentStatus.Suggested)
            .Select(a => a.AssignedToId)
            .Distinct()
            .CountAsync(cancellationToken);

        if (distinctMentors < MinimumMentors)
        {
            metrics.SampleSizeRefused();

            // No partial data of any kind: «team of five minus team of four» is precisely the
            // differential attack the whole-or-nothing rule closes (ANA-013).
            throw new ForbiddenException(
                ErrorCodes.InsufficientSampleSize,
                "Недостаточно данных для обезличенного отчёта: требуется не менее пяти менторов.");
        }
    }

    // -----------------------------------------------------------------
    // Period and assembly
    // -----------------------------------------------------------------

    private async Task<ReportDto> BuildAsync(
        ReportScope scope,
        ReportQuery query,
        CancellationToken cancellationToken)
    {
        var timeZoneId = await ResolvePeriodZoneAsync(scope, cancellationToken);
        var (from, to) = ResolvePeriod(query, timeZoneId);

        // ANA-002: local dates become a half-open UTC interval. Half-open rather than inclusive on
        // both ends, so an event at 23:59:59.999 is neither lost nor counted twice at the boundary.
        var fromUtc = deadlines.ToUtc(from, TimeOnly.MinValue, timeZoneId);
        var toUtc = deadlines.ToUtc(to.AddDays(1), TimeOnly.MinValue, timeZoneId);

        var filters = new ReportFilters(query.IncludeCancelled, query.Source, query.AssignmentType);

        // An ungrouped report has a total and no breakdown. The query still returns one all-null row,
        // and echoing it as a breakdown line would duplicate the total under a row with nothing to
        // identify it — for a Mentor's anonymised report that row is precisely what must not exist.
        var rows = scope.Grouping is ReportGrouping.None
            ? []
            : await metricsQuery.ExecuteAsync(scope, fromUtc, toUtc, filters, cancellationToken);

        var totals = await metricsQuery.ExecuteAsync(
            scope with { Grouping = ReportGrouping.None },
            fromUtc,
            toUtc,
            filters,
            cancellationToken);

        var branches = await LoadBranchesAsync(rows, cancellationToken);
        var categories = await LoadCategoriesAsync(rows, cancellationToken);
        var mentors = await LoadMentorsAsync(rows, cancellationToken);

        var today = DateOnly.FromDateTime(deadlines.ToLocal(clock.UtcNow, timeZoneId).DateTime);

        return new ReportDto(
            from,
            to,
            timeZoneId,

            // ANA-006: a period that has not finished is marked, so a comparison against it is known
            // to be approximate rather than quietly wrong.
            to >= today,
            scope.IsCrossBranch,
            [.. rows.Select(r => r.BranchId).OfType<Guid>().Distinct()],
            MetricsMapper.ToDto(totals.FirstOrDefault() ?? new MetricsRow()),
            [.. rows.Select(r => new ReportRowDto(
                r.BranchId is { } branchId ? branches.GetValueOrDefault(branchId) : null,
                r.CategoryId,
                r.CategoryId is { } categoryId ? categories.GetValueOrDefault(categoryId) : null,
                r.MentorId,
                r.MentorId is { } mentorId ? mentors.GetValueOrDefault(mentorId) : null,
                MetricsMapper.ToDto(r))),
            ]);
    }

    /// <summary>
    /// The zone the period is measured in (<c>TEN-074</c>).
    /// </summary>
    /// <remarks>
    /// A branch report uses the branch's zone; an organization report uses the head office's. Mixing
    /// zones inside one report without saying which is the base is forbidden, which is why the answer
    /// always carries <c>periodTimeZoneId</c>.
    /// </remarks>
    private async Task<string> ResolvePeriodZoneAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        var branches = dbContext.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrganizationId == scope.OrganizationId);

        var zone = scope.BranchId is { } branchId
            ? await branches.Where(b => b.Id == branchId).Select(b => b.TimeZoneId).FirstOrDefaultAsync(cancellationToken)
            : await branches.Where(b => b.IsHeadOffice).Select(b => b.TimeZoneId).FirstOrDefaultAsync(cancellationToken);

        return zone ?? "UTC";
    }

    /// <summary>Defaults to the last thirty days when the caller names no period.</summary>
    private (DateOnly From, DateOnly To) ResolvePeriod(ReportQuery query, string timeZoneId)
    {
        var today = DateOnly.FromDateTime(deadlines.ToLocal(clock.UtcNow, timeZoneId).DateTime);

        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(-29);

        if (from > to)
        {
            throw new ValidationAppException("from", "Начало периода не может быть позже его конца.");
        }

        return (from, to);
    }

    private async Task<Dictionary<Guid, BranchSummaryDto>> LoadBranchesAsync(
        IReadOnlyCollection<MetricsRow> rows,
        CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.BranchId).OfType<Guid>().Distinct().ToArray();

        return await dbContext.Branches
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => new BranchSummaryDto(b.Id, b.Name, b.Code, b.IsHeadOffice))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
    }

    private async Task<Dictionary<Guid, string>> LoadCategoriesAsync(
        IReadOnlyCollection<MetricsRow> rows,
        CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.CategoryId).OfType<Guid>().Distinct().ToArray();

        return await dbContext.Categories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
    }

    private async Task<Dictionary<Guid, string>> LoadMentorsAsync(
        IReadOnlyCollection<MetricsRow> rows,
        CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.MentorId).OfType<Guid>().Distinct().ToArray();

        return await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
    }

    private ICurrentUserContext RequireActor() => currentUser.Current ?? throw new UnauthorizedException();
}
