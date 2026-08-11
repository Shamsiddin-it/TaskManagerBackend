using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Tenancy;

/// <inheritdoc />
public sealed class TenantStateGuard(
    MentorTaskFlowDbContext dbContext,
    IBranchContext branchContext) : ITenantStateGuard
{
    public async Task EnsureWritableAsync(Guid? branchId, Guid? categoryId, CancellationToken cancellationToken)
    {
        await EnsureOrganizationActiveAsync(cancellationToken);

        if (branchId is { } branch)
        {
            var isActive = await dbContext.Branches
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(b => b.Id == branch && b.OrganizationId == branchContext.EffectiveOrganizationId)
                .Select(b => b.IsActive)
                .FirstOrDefaultAsync(cancellationToken);

            if (!isActive)
            {
                // BRN-032: takes priority over CATEGORY_INACTIVE. A category inside a deactivated
                // branch is unusable regardless of its own flag, and naming the category would send
                // an administrator to fix the wrong thing.
                throw new ForbiddenException(
                    ErrorCodes.BranchInactive,
                    "Филиал деактивирован: операции записи в его контуре недоступны.");
            }
        }

        if (categoryId is { } category)
        {
            var isActive = await dbContext.Categories
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => c.Id == category && c.OrganizationId == branchContext.EffectiveOrganizationId)
                .Select(c => c.IsActive)
                .FirstOrDefaultAsync(cancellationToken);

            if (!isActive)
            {
                throw new ForbiddenException(
                    ErrorCodes.CategoryInactive,
                    "Категория деактивирована: операции записи в её контуре недоступны.");
            }
        }
    }

    public async Task EnsureOrganizationActiveAsync(CancellationToken cancellationToken)
    {
        var isActive = await dbContext.Organizations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.Id == branchContext.EffectiveOrganizationId)
            .Select(o => o.IsActive)
            .FirstOrDefaultAsync(cancellationToken);

        if (!isActive)
        {
            // ORG-007: unlike the other two levels this also blocks reads. Deactivating an
            // organization is an operational act, not a product feature.
            throw new ForbiddenException(
                ErrorCodes.OrganizationInactive,
                "Организация деактивирована.");
        }
    }
}
