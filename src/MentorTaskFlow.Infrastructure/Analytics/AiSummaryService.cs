using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Analytics;
using MentorTaskFlow.Domain.Analytics;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Analytics;

/// <inheritdoc />
public sealed class AiSummaryService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    IAnalyticsService analytics,
    IAiSummaryProvider provider,
    IAuditWriter auditWriter,
    AiMetrics metrics,
    IOptions<AiOptions> options,
    IClock clock) : IAiSummaryService
{
    /// <summary>Named in the cache key so a future second kind of report cannot collide with this one.</summary>
    private const string ReportType = "period-summary";

    /// <summary>The privacy threshold of <c>ANA-012</c>, applied per branch by <c>TEN-078</c>.</summary>
    private const int MinimumMentors = 5;

    private readonly AiOptions _options = options.Value;

    public async Task<AiSummaryResult> GenerateAsync(AiSummaryRequest request, CancellationToken cancellationToken)
    {
        var actor = currentUser.Current ?? throw new UnauthorizedException();
        var organizationId = branchContext.EffectiveOrganizationId;

        // TEN-077, and the order matters as much as the check: a Branch Admin asking about another
        // branch is refused here, before the cache is consulted and before the provider is called.
        // A 404 issued after a cache lookup still discloses, through timing, that the report existed.
        var scope = await ResolveScopeAsync(actor, request, organizationId, cancellationToken);

        var report = await LoadReportAsync(actor, request, scope, cancellationToken);
        var previous = await LoadPreviousAsync(actor, request, scope, report, cancellationToken);

        var input = await BuildInputAsync(scope, report, previous, cancellationToken);
        var built = AiPromptBuilder.Build(input, _options);

        var cacheKey = AiSummaryCacheKey.Build(
            organizationId,
            scope.BranchId,
            scope.CategoryId,
            scope.Scope,
            scope.SubjectUserId,
            ReportType,
            report.From,
            report.To,
            built.MetricsHash,
            provider.PromptVersion,
            provider.ModelId);

        var existing = await FindAsync(organizationId, cacheKey, cancellationToken);

        if (existing is { Status: AiSummaryStatus.Completed } cached && !request.Force)
        {
            metrics.CacheHit(scope.Scope.ToString());

            return new AiSummaryResult(ToDto(cached, report, fromCache: true), WasCreated: false);
        }

        if (request.Force)
        {
            await EnsureForceAllowedAsync(organizationId, scope, cancellationToken);
        }

        return await GenerateAsync(actor, scope, report, built, cacheKey, existing, request.Force, cancellationToken);
    }

    // -----------------------------------------------------------------
    // Scope and access (AI-020, TEN-077, TEN-078)
    // -----------------------------------------------------------------

    /// <summary>What the report is about, once the caller's role has had its say.</summary>
    private sealed record SummaryScope
    {
        public required Guid OrganizationId { get; init; }

        public Guid? BranchId { get; init; }

        public Guid? CategoryId { get; init; }

        public Guid? SubjectUserId { get; init; }

        public required AiSummaryScope Scope { get; init; }
    }

    /// <summary>
    /// Maps the request onto a scope the caller actually holds (<c>AI-020</c>).
    /// </summary>
    /// <remarks>
    /// Refusals here are 404 rather than 403 wherever the answer would otherwise confirm that a branch
    /// exists (<c>TEN-006</c>, <c>TEN-077</c>). A Branch Admin who guesses another branch's id learns
    /// nothing from the response that they did not already put into the request.
    /// </remarks>
    private async Task<SummaryScope> ResolveScopeAsync(
        ICurrentUserContext actor,
        AiSummaryRequest request,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var isOrganizationAdmin = actor is { Role: UserRole.Admin, AdminScope: AdminScope.Organization };
        var isBranchAdmin = actor is { Role: UserRole.Admin, AdminScope: AdminScope.Branch };

        return request.Scope switch
        {
            AiSummaryScopeDto.Personal => await PersonalAsync(actor, request, organizationId, cancellationToken),

            AiSummaryScopeDto.Team => new SummaryScope
            {
                OrganizationId = organizationId,
                BranchId = ResolveBranch(actor, request, isOrganizationAdmin),
                CategoryId = ResolveCategory(actor, request, isOrganizationAdmin || isBranchAdmin),
                Scope = AiSummaryScope.Team,
            },

            // A Lead or a Mentor has no branch-wide view of any kind: the metrics endpoints do not
            // offer one either, and a summary is not a way around that.
            AiSummaryScopeDto.Branch when isOrganizationAdmin || isBranchAdmin => new SummaryScope
            {
                OrganizationId = organizationId,
                BranchId = ResolveBranch(actor, request, isOrganizationAdmin),
                Scope = AiSummaryScope.Branch,
            },

            AiSummaryScopeDto.Organization when isOrganizationAdmin => new SummaryScope
            {
                OrganizationId = organizationId,
                Scope = AiSummaryScope.Organization,
            },

            // The aggregate over every branch is the Organization Admin's report alone (TEN-078).
            // For a Branch Admin the organization is not a scope they hold, so it does not exist.
            AiSummaryScopeDto.Organization => throw new NotFoundException(),

            _ => throw new ForbiddenException(
                detail: "Резюме этого уровня недоступно для текущей роли."),
        };
    }

    /// <summary>
    /// Resolves the subject of a personal report and takes the report's scope from <b>them</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The branch and the category come from the subject rather than from the request. A personal
    /// report is about one person's work, and that work happened in their branch and their category —
    /// letting the caller name a different pair would produce a row filed under a scope the figures
    /// do not belong to, and <c>ck_ai_summaries_scope_shape</c> would be satisfied by it.
    /// </para>
    /// <para>
    /// The lookup runs through the tenant-filtered <c>Users</c> set, so a subject outside the caller's
    /// organization and branch simply is not found — 404, the same answer as for a subject who does
    /// not exist (<c>TEN-006</c>).
    /// </para>
    /// </remarks>
    private async Task<SummaryScope> PersonalAsync(
        ICurrentUserContext actor,
        AiSummaryRequest request,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        // ANA-013: a Mentor naming anyone else is a 400, not a quietly narrowed answer. Narrowing
        // would let a caller walk the team one id at a time and rebuild the anonymised aggregate.
        if (actor.Role is UserRole.Mentor && request.MentorId is { } named && named != actor.UserId)
        {
            throw new ValidationAppException("mentorId", "Ментор видит только собственные показатели.");
        }

        var subjectId = actor.Role is UserRole.Mentor
            ? actor.UserId
            : request.MentorId ?? throw new ValidationAppException("mentorId", "Укажите ментора для персонального отчёта.");

        var subject = await dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == subjectId)
            .Select(u => new { u.BranchId, u.CategoryId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        if (subject.BranchId is null || subject.CategoryId is null)
        {
            throw new ValidationAppException("mentorId", "Персональный отчёт строится только по ментору или лиду.");
        }

        // AI-020: a Lead's remit is their own team. Another category's mentor is outside it, and the
        // answer is 404 rather than 403 so the request cannot be used to enumerate other categories.
        if (actor.Role is UserRole.Lead && subject.CategoryId != actor.CategoryId)
        {
            throw new NotFoundException();
        }

        return new SummaryScope
        {
            OrganizationId = organizationId,
            BranchId = subject.BranchId,
            CategoryId = subject.CategoryId,
            SubjectUserId = subjectId,
            Scope = AiSummaryScope.Personal,
        };
    }

    /// <summary>
    /// Which branch the report is about (<c>TEN-070</c>, <c>TEN-077</c>).
    /// </summary>
    /// <remarks>
    /// A branch id from anyone but an Organization Admin is refused rather than ignored. The metric
    /// endpoints ignore it, and can: they answer with figures that are visibly the caller's own. A
    /// summary is prose, and prose about the head office headed «Худжанд» is worse than no answer.
    /// </remarks>
    private static Guid ResolveBranch(
        ICurrentUserContext actor,
        AiSummaryRequest request,
        bool isOrganizationAdmin)
    {
        if (!isOrganizationAdmin && request.BranchId is { } requested && requested != actor.BranchId)
        {
            throw new NotFoundException();
        }

        return (isOrganizationAdmin ? request.BranchId ?? actor.BranchId : actor.BranchId)

            // An Organization Admin has no branch of their own, so a branch-scoped summary needs one
            // named — the same reason TEN-033 asks for X-MTF-Branch-Id on a branch-scoped mutation.
            ?? throw new BranchContextRequiredException();
    }

    private static Guid? ResolveCategory(ICurrentUserContext actor, AiSummaryRequest request, bool isAdmin) =>
        isAdmin
            ? request.CategoryId ?? throw new ValidationAppException("categoryId", "Укажите категорию для командного резюме.")
            : actor.CategoryId;

    // -----------------------------------------------------------------
    // Metrics (21) — the model never computes one (22.1)
    // -----------------------------------------------------------------

    /// <summary>
    /// Fetches the figures through the analytics service rather than re-querying them.
    /// </summary>
    /// <remarks>
    /// Deliberate reuse: the summary must be about the same numbers the analytics page shows, and the
    /// scope rules — <c>ANA-012</c>'s five-mentor threshold in particular — are applied there. A second
    /// query path would be a second set of rules to keep in step, and the first divergence would be a
    /// summary quoting figures the reader cannot find.
    /// </remarks>
    private Task<ReportDto> LoadReportAsync(
        ICurrentUserContext actor,
        AiSummaryRequest request,
        SummaryScope scope,
        CancellationToken cancellationToken)
    {
        var query = new ReportQuery
        {
            From = request.From,
            To = request.To,
            BranchId = scope.BranchId,
            CategoryId = scope.CategoryId,
            MentorId = scope.SubjectUserId,
            IncludeCancelled = request.IncludeCancelled,
        };

        return scope.Scope switch
        {
            AiSummaryScope.Personal => analytics.GetPersonalAsync(query, cancellationToken),
            AiSummaryScope.Organization => analytics.GetBranchComparisonAsync(query, cancellationToken),
            _ => analytics.GetTeamAsync(query, cancellationToken),
        };
    }

    /// <summary>
    /// The same span immediately before, for the dynamics of 22.1.
    /// </summary>
    /// <remarks>
    /// Equal in length rather than «the previous month», so the comparison is between spans of the
    /// same size. A fourteen-day report compared against a thirty-day one would show a fall in every
    /// count and none of it would mean anything.
    /// </remarks>
    private async Task<ReportDto?> LoadPreviousAsync(
        ICurrentUserContext actor,
        AiSummaryRequest request,
        SummaryScope scope,
        ReportDto current,
        CancellationToken cancellationToken)
    {
        var length = current.To.DayNumber - current.From.DayNumber + 1;

        var previous = request with
        {
            To = current.From.AddDays(-1),
            From = current.From.AddDays(-length),
        };

        try
        {
            return await LoadReportAsync(actor, previous, scope, cancellationToken);
        }
        catch (ForbiddenException)
        {
            // The threshold can be met this period and not the one before — a team that grew past
            // five. The current report stands; the comparison is simply absent (ANA-012).
            return null;
        }
    }

    // -----------------------------------------------------------------
    // The prompt input (22.3, AI-006)
    // -----------------------------------------------------------------

    private async Task<AiPromptInput> BuildInputAsync(
        SummaryScope scope,
        ReportDto report,
        ReportDto? previous,
        CancellationToken cancellationToken)
    {
        var isOrganization = scope.Scope is AiSummaryScope.Organization;

        return new AiPromptInput
        {
            Scope = scope.Scope.ToString(),
            From = report.From,
            To = report.To,
            TimeZoneId = report.PeriodTimeZoneId,
            IsPartialPeriod = report.IsPartialPeriod,
            Current = report.Total,
            Previous = previous?.Total,
            Groups = BuildGroups(scope, report, await CountMentorsPerBranchAsync(scope, report, cancellationToken)),
            Topics = await LoadTopicsAsync(scope, report, cancellationToken),

            // TEN-078: an organization-wide report carries no branch's review text. An Organization
            // Admin who wants comments asks for the branch report explicitly.
            Comments = isOrganization ? [] : await LoadCommentsAsync(scope, report, cancellationToken),
        };
    }

    /// <summary>
    /// Anonymised breakdown lines (22.3, <c>TEN-078</c>).
    /// </summary>
    /// <remarks>
    /// Labels are positional and local to this report: <c>Ментор 1</c> in one branch's report and
    /// <c>Ментор 1</c> in another's are unrelated, which is what stops two reports being joined into a
    /// named list. For the organization aggregate the unit is the branch, and branches carry their
    /// human-readable name — permitted by <c>TEN-079</c>, unlike the code, the address and the id.
    /// </remarks>
    private static IReadOnlyList<AiPromptGroup> BuildGroups(
        SummaryScope scope,
        ReportDto report,
        IReadOnlyDictionary<Guid, int> mentorsPerBranch)
    {
        if (scope.Scope is AiSummaryScope.Organization)
        {
            // Only branches that clear the threshold on their own. Аn aggregate assembled from a
            // three-mentor branch is that branch's three mentors described one level up.
            return
            [
                .. report.Rows
                    .Where(row => row.Branch is { } branch && mentorsPerBranch.GetValueOrDefault(branch.Id) >= MinimumMentors)
                    .Select(row => new AiPromptGroup(row.Branch!.Name, row.Metrics)),
            ];
        }

        return
        [
            .. report.Rows.Select((row, index) => new AiPromptGroup(
                row.MentorId is not null ? $"Ментор {index + 1}" : $"Группа {index + 1}",
                row.Metrics)),
        ];
    }

    private async Task<IReadOnlyDictionary<Guid, int>> CountMentorsPerBranchAsync(
        SummaryScope scope,
        ReportDto report,
        CancellationToken cancellationToken)
    {
        if (scope.Scope is not AiSummaryScope.Organization)
        {
            return new Dictionary<Guid, int>();
        }

        var branchIds = report.Rows.Select(r => r.Branch?.Id).OfType<Guid>().Distinct().ToArray();

        return await dbContext.Assignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.OrganizationId == scope.OrganizationId && branchIds.Contains(a.BranchId))
            .Where(a => a.Status != AssignmentStatus.Draft && a.Status != AssignmentStatus.Suggested)
            .GroupBy(a => a.BranchId)
            .Select(g => new { BranchId = g.Key, Mentors = g.Select(a => a.AssignedToId).Distinct().Count() })
            .ToDictionaryAsync(x => x.BranchId, x => x.Mentors, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> LoadTopicsAsync(
        SummaryScope scope,
        ReportDto report,
        CancellationToken cancellationToken) =>
        await ScopedAssignments(scope, report)
            .Where(a => a.Title != null)
            .Select(a => a.Title)
            .Distinct()
            .OrderBy(title => title)
            .Take(50)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Review comments of the period, capped before they leave the database (<c>AI-007</c>).
    /// </summary>
    /// <remarks>
    /// The count limit is applied in SQL rather than after loading: a busy category has thousands of
    /// comments, and reading them all to discard all but fifty pulls the same personal data through
    /// the process for no purpose.
    /// </remarks>
    private async Task<IReadOnlyList<string>> LoadCommentsAsync(
        SummaryScope scope,
        ReportDto report,
        CancellationToken cancellationToken)
    {
        var assignmentIds = ScopedAssignments(scope, report).Select(a => a.Id);

        return await dbContext.Reviews
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.OrganizationId == scope.OrganizationId)
            .Where(r => assignmentIds.Contains(r.AssignmentId))
            .Where(r => r.Comment != null)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.Comment!)
            .Take(_options.MaxComments)
            .ToListAsync(cancellationToken);
    }

    private IQueryable<Assignment> ScopedAssignments(SummaryScope scope, ReportDto report) =>
        dbContext.Assignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.OrganizationId == scope.OrganizationId)
            .Where(a => scope.BranchId == null || a.BranchId == scope.BranchId)
            .Where(a => scope.CategoryId == null || a.CategoryId == scope.CategoryId)
            .Where(a => scope.SubjectUserId == null || a.AssignedToId == scope.SubjectUserId)
            .Where(a => a.Status != AssignmentStatus.Draft && a.Status != AssignmentStatus.Suggested)
            .Where(a => a.CreatedAt >= report.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
                        && a.CreatedAt < report.To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    // -----------------------------------------------------------------
    // Cache, limit and generation (AI-010, AI-011, AI-013)
    // -----------------------------------------------------------------

    private Task<AiSummary?> FindAsync(Guid organizationId, string cacheKey, CancellationToken cancellationToken) =>
        dbContext.AiSummaries
            .IgnoreQueryFilters()
            .Where(s => s.OrganizationId == organizationId && s.CacheKey == cacheKey)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// One forced regeneration per subject per day (<c>AI-011</c>).
    /// </summary>
    /// <remarks>
    /// Counted across periods, because the subject is what the limit names. Counting per row would let
    /// the same person be regenerated all afternoon by shifting the period by a day each time, which
    /// is exactly the spend the rule exists to bound.
    /// </remarks>
    private async Task EnsureForceAllowedAsync(
        Guid organizationId,
        SummaryScope scope,
        CancellationToken cancellationToken)
    {
        var since = clock.UtcNow.AddDays(-1);

        var forced = await dbContext.AiSummaries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.OrganizationId == organizationId)
            .Where(s => s.Scope == scope.Scope)
            .Where(s => s.BranchId == scope.BranchId)
            .Where(s => s.CategoryId == scope.CategoryId)
            .Where(s => s.SubjectUserId == scope.SubjectUserId)
            .Where(s => s.LastForcedAt != null && s.LastForcedAt > since)
            .CountAsync(cancellationToken);

        if (forced >= _options.ForceRegenerationPerDay)
        {
            throw new AiRegenerationLimitException();
        }
    }

    private async Task<AiSummaryResult> GenerateAsync(
        ICurrentUserContext actor,
        SummaryScope scope,
        ReportDto report,
        AiPromptBuildResult built,
        string cacheKey,
        AiSummary? existing,
        bool force,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var summary = existing ?? AiSummary.Start(
            scope.OrganizationId,
            scope.BranchId,
            scope.CategoryId,
            scope.Scope,
            scope.SubjectUserId,
            report.From,
            report.To,
            cacheKey,
            built.MetricsHash,
            provider.PromptVersion,
            provider.ModelId,
            actor.UserId,
            now);

        if (existing is null)
        {
            dbContext.AiSummaries.Add(summary);
        }

        if (force)
        {
            summary.MarkForced(now);
        }

        AiSummaryCompletion completion;

        try
        {
            completion = await provider.GenerateAsync(built.Prompt, cancellationToken);
        }
        catch (AppException failure)
        {
            // The failed attempt is recorded and committed: AI-021 asks for every call to be audited,
            // and a failure nobody can see is a failure nobody will fix.
            summary.Fail(failure.Code);
            Audit(summary, force, completion: null, failure.Code);
            await dbContext.SaveChangesAsync(cancellationToken);

            throw;
        }

        summary.Complete(completion.Content, completion.InputTokens, completion.OutputTokens);
        Audit(summary, force, completion, failureReason: null);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (existing is null)
        {
            // Приложение N, race 15: two callers generated the same report at once and one lost the
            // unique index. The loser reads the winner's row rather than failing — both callers asked
            // the same question and there is now an answer to it.
            dbContext.ChangeTracker.Clear();

            var winner = await FindAsync(scope.OrganizationId, cacheKey, cancellationToken)
                ?? throw new ServiceUnavailableException(
                    Contracts.Common.ErrorCodes.AiProviderUnavailable,
                    "Не удалось сохранить резюме. Повторите попытку.");

            return new AiSummaryResult(ToDto(winner, report, fromCache: true), WasCreated: false);
        }

        metrics.Generated(scope.Scope.ToString());

        return new AiSummaryResult(ToDto(summary, report, fromCache: false), WasCreated: existing is null);
    }

    /// <summary>Records the call, its cost and its outcome (<c>AI-021</c>).</summary>
    /// <remarks>
    /// The metadata carries no prompt and no content — only what the call was and what it cost. The
    /// prompt is the organization's data and the audit trail is read by administrators of the branch,
    /// not of the category the report was about (<c>AUD-022</c>).
    /// </remarks>
    private void Audit(AiSummary summary, bool force, AiSummaryCompletion? completion, string? failureReason) =>
        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.AiSummaryGenerate,
            EntityType = nameof(AiSummary),
            EntityId = summary.Id,
            CategoryId = summary.CategoryId,
            BranchId = summary.BranchId,
            Result = failureReason is null ? AuditResult.Success : AuditResult.Failure,
            FailureReason = failureReason,
            Metadata = JsonSerializer.SerializeToDocument(new
            {
                scope = summary.Scope.ToString(),
                modelId = summary.ModelId,
                promptVersion = summary.PromptVersion,
                inputTokens = completion?.InputTokens,
                outputTokens = completion?.OutputTokens,

                // AI-004: the provider's own identifier for the call, which is what a question about
                // a particular report is answered with.
                providerRequestId = completion?.RequestId,
                force,
            }),
        });

    private static AiSummaryDto ToDto(AiSummary summary, ReportDto report, bool fromCache) => new(
        summary.Id,
        (AiSummaryScopeDto)summary.Scope,
        summary.PeriodStart,
        summary.PeriodEnd,
        report.PeriodTimeZoneId,
        report.IsPartialPeriod,
        summary.ModelId,
        summary.PromptVersion,
        fromCache,
        summary.CreatedAt,
        summary.Content ?? string.Empty,
        report.Total,
        AiSummaryDto.StandardDisclaimer);
}
