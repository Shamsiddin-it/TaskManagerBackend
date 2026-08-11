using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Tenancy;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Tenancy;

/// <inheritdoc />
public sealed class OrganizationService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    IAuditWriter auditWriter,
    IClock clock) : IOrganizationService
{
    public async Task<object> GetAsync(CancellationToken cancellationToken)
    {
        var user = currentUser.Current ?? throw new UnauthorizedException();
        var organization = await FindAsync(cancellationToken);

        // ORG-003: the full profile only for an Organization Admin. Slug, IsActive and the timestamps
        // serve no scenario of a Lead, Mentor or Branch Admin, and withholding them keeps the
        // disclosure surface as small as the requirement allows.
        return user is { Role: UserRole.Admin, AdminScope: AdminScope.Organization }
            ? ToDto(organization)
            : new OrganizationSummaryDto(organization.Id, organization.Name);
    }

    public async Task<OrganizationDto> UpdateAsync(UpdateOrganizationRequest request, CancellationToken cancellationToken)
    {
        var organization = await FindAsync(cancellationToken, tracked: true);
        dbContext.Expect(organization, request.ConcurrencyToken);

        var previousName = organization.Name;

        // Only the name is reachable. Slug is immutable and IsActive belongs to provisioning, so
        // neither is a parameter of the domain method either (ORG-004, ORG-020).
        organization.Rename(request.Name, clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.OrganizationUpdate,
            EntityType = nameof(Organization),
            EntityId = organization.Id,
            Metadata = JsonSerializer.SerializeToDocument(new
            {
                previousName,
                newName = organization.Name,
            }),
        });

        await dbContext.SaveWithConcurrencyCheckAsync(organization, cancellationToken);

        return ToDto(organization);
    }

    private async Task<Organization> FindAsync(CancellationToken cancellationToken, bool tracked = false)
    {
        var source = tracked ? dbContext.Organizations : dbContext.Organizations.AsNoTracking();

        // The identifier comes from the principal and is never a parameter, so there is no request
        // shape that could ask about another organization (ORG-021).
        return await source.FirstOrDefaultAsync(
                   o => o.Id == branchContext.EffectiveOrganizationId,
                   cancellationToken)
               ?? throw new NotFoundException();
    }

    private OrganizationDto ToDto(Organization organization) => new(
        organization.Id,
        organization.Name,
        organization.Slug,
        organization.IsActive,
        organization.CreatedAt,
        dbContext.Read(organization));
}
