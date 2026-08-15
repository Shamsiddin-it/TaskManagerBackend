using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Scheduling;

/// <summary>
/// Balanced deterministic assignment (TZ 20.4).
/// </summary>
/// <remarks>
/// <para>
/// Version 2.0 called this round-robin while describing a least-loaded choice — different algorithms.
/// The name was corrected and the behaviour kept: for study tasks, balancing by load is more useful
/// than cycling, because a mentor who is already behind should not be handed more.
/// </para>
/// <para>
/// <c>TEN-051</c> is the load-bearing part: the candidate set is bounded by organization, branch
/// <b>and</b> category. A mentor of another branch can never be chosen, however identically their
/// category is named.
/// </para>
/// </remarks>
public sealed class MentorSelector(MentorTaskFlowDbContext dbContext)
{
    /// <summary>Statuses that count as load — everything an assignment can be while still open.</summary>
    private static readonly AssignmentStatus[] NonTerminal =
    [
        AssignmentStatus.Assigned,
        AssignmentStatus.Submitted,
        AssignmentStatus.InReview,
        AssignmentStatus.NeedsRework,
        AssignmentStatus.Overdue,
    ];

    /// <summary>
    /// Picks the mentor to receive the next suggestion, or null when the category has none active.
    /// </summary>
    /// <remarks>
    /// One aggregating query rather than a query per mentor (<c>SCH-015</c>), and a total order rather
    /// than a tie-break by chance: fewest open tasks, then the earliest last assignment — with never
    /// assigned counting as earliest, so a newcomer is served first — then the smallest identifier.
    /// The last step exists purely to make the outcome reproducible, which is what
    /// <c>TEST-SCH-004</c> checks.
    /// </remarks>
    public async Task<Guid?> SelectAsync(
        Guid organizationId,
        Guid branchId,
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        var candidates = dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.OrganizationId == organizationId
                        && u.BranchId == branchId
                        && u.CategoryId == categoryId
                        && u.Role == UserRole.Mentor
                        && u.IsActive);

        var assignments = dbContext.Assignments.IgnoreQueryFilters().AsNoTracking();

        var ranked = await candidates
            .Select(u => new
            {
                u.Id,
                OpenCount = assignments.Count(a => a.AssignedToId == u.Id && NonTerminal.Contains(a.Status)),

                LastAssignedAt = assignments
                    .Where(a => a.AssignedToId == u.Id && a.AssignedAt != null)
                    .Max(a => (DateTimeOffset?)a.AssignedAt),
            })
            .OrderBy(x => x.OpenCount)

            // Ascending order puts NULLs *last* in PostgreSQL, which would serve a newcomer last —
            // the opposite of SCH-013. Ordering on «has a value» first (false before true) makes
            // «never assigned» count as the earliest, portably and without a raw NULLS FIRST clause.
            .ThenBy(x => x.LastAssignedAt.HasValue)
            .ThenBy(x => x.LastAssignedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return ranked?.Id;
    }
}
