using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Notifications;
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
    IAuditWriter auditWriter,
    IOutboxWriter outboxWriter,
    IOptions<BootstrapOptions> options,
    IClock clock,
    ILogger<BootstrapProvisioner> logger)
{
    private readonly BootstrapOptions _options = options.Value;

    public Task<BootstrapResult> ProvisionAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsComplete)
        {
            return Task.FromResult(new BootstrapResult(false, null, "Bootstrap configuration is incomplete."));
        }

        // The connection is configured with EnableRetryOnFailure, and a retrying execution strategy
        // refuses a user-initiated transaction unless the whole unit is handed to it: on a transient
        // failure it has to replay everything, and it cannot replay a transaction it does not own.
        // Without this wrapper the provisioning below throws before touching the database.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(() => ProvisionCoreAsync(cancellationToken));
    }

    private async Task<BootstrapResult> ProvisionCoreAsync(CancellationToken cancellationToken)
    {
        // Cleared on entry rather than once at the top: the execution strategy may replay this whole
        // method, and entities staged by a failed attempt would otherwise be inserted twice.
        //
        // The tenant filter is suppressed for the duration — provisioning is one of the registered
        // system tasks of SEC-031, running with no principal, and must see the whole table to decide
        // whether to act at all.
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

            // Steps 5 and 6 of DEPLOY-030. Both carry BranchId = NULL: the first administrator is an
            // Organization Admin who belongs to no branch, so the invitation and the provisioning
            // record are organization-level by construction (TEN-042, TEN-048).
            outboxWriter.EnqueueSystem(
                new OutboxEntry
                {
                    RecipientUserId = admin.Id,
                    EventType = NotificationEventTypes.UserInvitation,
                    Channel = NotificationChannel.Email,
                    DeduplicationKey = $"invitation:{admin.Id:N}",

                    // The payload names the organization but carries no token and no link: NTF-017
                    // forbids putting either in a notification payload, and the renderer builds the
                    // link from the security token when it sends.
                    Payload = JsonSerializer.SerializeToDocument(new
                    {
                        organizationName = organization.Name,
                        fullName = admin.FullName,
                    }),
                },
                organization.Id,
                branchId: null);

            auditWriter.WriteSystem(
                new AuditEntry
                {
                    Action = AuditActions.BootstrapProvision,
                    EntityType = nameof(Organization),
                    EntityId = organization.Id,
                    Metadata = JsonSerializer.SerializeToDocument(new
                    {
                        organizationSlug = organization.Slug,
                        headOfficeCode = headOffice.Code,
                    }),
                },
                organization.Id,
                branchId: null);

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
