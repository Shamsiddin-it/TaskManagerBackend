using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Identity;

/// <summary>Outcome of a provisioning attempt.</summary>
public sealed record BootstrapResult(bool Provisioned, string? SetPasswordLink, string? SkipReason);

/// <summary>
/// Creates the first organization, its head office and the first administrator (TZ 32.6).
/// </summary>
/// <remarks>
/// <para>
/// Runs once from the <c>mtf-migrator</c> container against an empty <c>organizations</c> table; with
/// any organization present the step is skipped (<c>DEPLOY-022</c>).
/// </para>
/// <para>
/// The first administrator is an <b>Organization</b> Admin, not an administrator of the head office.
/// An admin pinned to a branch could not create a second branch, and the organization would be stuck
/// on one branch with no way to grow (<c>DEPLOY-031</c>).
/// </para>
/// </remarks>
public sealed class BootstrapProvisioner(
    MentorTaskFlowDbContext dbContext,
    AuthService authService,
    IOptions<BootstrapOptions> options,
    IClock clock,
    ILogger<BootstrapProvisioner> logger)
{
    private readonly BootstrapOptions _options = options.Value;

    public async Task<BootstrapResult> ProvisionAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsComplete)
        {
            return new BootstrapResult(false, null, "Bootstrap configuration is incomplete.");
        }

        // Suppressed tenant filter: provisioning is one of the registered system tasks of SEC-031 —
        // it runs with no principal and must observe the whole table to decide whether to act.
        dbContext.ChangeTracker.Clear();

        if (await dbContext.Organizations.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return new BootstrapResult(false, null, "An organization already exists; bootstrap skipped.");
        }

        var now = clock.UtcNow;

        // One transaction for the whole set. A partial result — an organization with no head office,
        // or an administrator with no way to set a password — is worse than no result at all
        // (DEPLOY-030, BRN-028).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var organization = Organization.Provision(_options.OrganizationName!, _options.OrganizationSlug!, now);
            dbContext.Organizations.Add(organization);

            var headOffice = Branch.CreateHeadOffice(
                organization.Id,
                _options.HeadOfficeName!,
                _options.HeadOfficeCode!,
                address: null,
                _options.HeadOfficeTimeZone!,
                now);
            dbContext.Branches.Add(headOffice);

            var admin = User.CreateOrganizationAdmin(
                organization.Id,
                "Администратор организации",
                _options.AdminEmail!,
                now);
            dbContext.Users.Add(admin);

            await dbContext.SaveChangesAsync(cancellationToken);

            var setPasswordLink = await authService.IssueSetPasswordLinkAsync(admin, ipAddress: null, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            // DEPLOY-024: the link is written to the migrator container log exactly once. Organization
            // and branch identifiers are deliberately absent from that line.
            logger.LogWarning(
                "Bootstrap complete. Set the first administrator password using this one-time link: {SetPasswordLink}",
                setPasswordLink);

            return new BootstrapResult(true, setPasswordLink, null);
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(cancellationToken);

            // Two migrator containers starting at once: the unique indexes ux_organizations_slug,
            // ux_branches_organization_code and ux_users_normalized_email are the final guard, and the
            // loser rolls back whole rather than leaving a half-provisioned tenant (DEPLOY-032).
            logger.LogInformation(
                exception,
                "Bootstrap lost a race with a concurrent provisioning run; no changes were applied.");

            return new BootstrapResult(false, null, "Concurrent bootstrap detected; this run made no changes.");
        }
    }
}
