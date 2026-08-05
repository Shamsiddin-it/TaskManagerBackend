using MentorTaskFlow.Application.Common.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Persistence;

/// <inheritdoc />
public sealed class BranchScopeValidator(MentorTaskFlowDbContext dbContext) : IBranchScopeValidator
{
    public Task<bool> BelongsToOrganizationAsync(Guid branchId, Guid organizationId, CancellationToken cancellationToken) =>
        // IgnoreQueryFilters is deliberate and safe here: this runs while the request scope is being
        // computed, so the filter is not yet populated, and the organization is pinned explicitly in
        // the predicate below. The explicit condition — not the filter — is the protection (SEC-030).
        dbContext.Branches
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(b => b.Id == branchId && b.OrganizationId == organizationId, cancellationToken);
}
