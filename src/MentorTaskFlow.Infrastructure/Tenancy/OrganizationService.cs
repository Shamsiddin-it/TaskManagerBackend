using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Tenancy;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Infrastructure.Persistence;
using MentorTaskFlow.Infrastructure.Persistence.Configurations;
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

        // xmin is projected by the query rather than read from the change tracker: the row is not
        // tracked, and Entry() on a detached entity would yield the default shadow value, handing the
        // client a token that is refused on its first write.
        var row = await dbContext.Organizations
            .AsNoTracking()
            .Where(o => o.Id == branchContext.EffectiveOrganizationId)
            .Select(o => new OrganizationRow(o, EF.Property<uint>(o, ConcurrencyTokenExtensions.PropertyName)))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        // ORG-003: the full profile only for an Organization Admin. Slug, IsActive and the timestamps
        // serve no scenario of a Lead, Mentor or Branch Admin, and withholding them keeps the
        // disclosure surface as small as the requirement allows.
        return user is { Role: UserRole.Admin, AdminScope: AdminScope.Organization }
            ? ToDto(row.Organization, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin))
            : new OrganizationSummaryDto(row.Organization.Id, row.Organization.Name);
    }

    public async Task<OrganizationDto> UpdateAsync(UpdateOrganizationRequest request, CancellationToken cancellationToken)
    {
        var organization = await FindTrackedAsync(cancellationToken);
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

        return ToDto(organization, dbContext.Read(organization));
    }

    /// <summary>
    /// Loads the caller's own organization for a write path.
    /// </summary>
    /// <remarks>
    /// The identifier comes from the principal and is never a parameter, so no request shape could ask
    /// about another organization (<c>ORG-021</c>).
    /// </remarks>
    private async Task<Organization> FindTrackedAsync(CancellationToken cancellationToken) =>
        await dbContext.Organizations.FirstOrDefaultAsync(
                o => o.Id == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

    /// <summary>Carries the shadow <c>xmin</c> out of a no-tracking query alongside its entity.</summary>
    private sealed record OrganizationRow(Organization Organization, uint Xmin);

    private static OrganizationDto ToDto(Organization organization, string concurrencyToken) => new(
        organization.Id,
        organization.Name,
        organization.Slug,
        organization.IsActive,
        organization.CreatedAt,
        concurrencyToken);
}
