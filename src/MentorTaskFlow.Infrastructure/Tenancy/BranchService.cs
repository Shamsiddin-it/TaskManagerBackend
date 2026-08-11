using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Tenancy;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MentorTaskFlow.Infrastructure.Tenancy;

/// <inheritdoc />
public sealed class BranchService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    IAuditWriter auditWriter,
    IOutboxWriter outboxWriter,
    ITimeZoneCatalog timeZones,
    IClock clock) : IBranchService
{
    private static readonly string[] SortableColumns = ["name", "code", "createdAt"];

    public async Task<PagedResult<BranchDto>> ListAsync(BranchListQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(query.Page, PaginationLimits.DefaultPage);
        var pageSize = Math.Clamp(query.PageSize, PaginationLimits.MinPageSize, PaginationLimits.MaxPageSize);

        // Explicit organization condition alongside the global query filter, never instead of it
        // (SEC-030).
        var source = dbContext.Branches
            .AsNoTracking()
            .Where(b => b.OrganizationId == branchContext.EffectiveOrganizationId);

        if (query.IsActive is { } isActive)
        {
            source = source.Where(b => b.IsActive == isActive);
        }

        source = ApplySort(source, query);

        var totalCount = await source.CountAsync(cancellationToken);

        var branches = await source
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // Projected after materialisation: the concurrency token is a shadow property and cannot be
        // read inside a LINQ projection.
        var items = branches.Select(ToDto).ToList();

        return new PagedResult<BranchDto>(items, page, pageSize, totalCount);
    }

    public async Task<object> GetAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var user = currentUser.Current ?? throw new UnauthorizedException();
        var branch = await FindInOrganizationAsync(branchId, cancellationToken);

        // A Lead or Mentor may read only their own branch, and only the minimal projection: address
        // and time zone play no part in their scenarios (BRN-009).
        if (user.Role is UserRole.Lead or UserRole.Mentor)
        {
            return branch.Id == user.BranchId
                ? ToSummary(branch)
                : throw new NotFoundException();
        }

        // A Branch Admin has GET on their own branch and nothing else. Any other identifier answers
        // 404, identical to a branch that does not exist (BRN-008, TEN-006).
        if (user is { Role: UserRole.Admin, AdminScope: AdminScope.Branch } && branch.Id != user.BranchId)
        {
            throw new NotFoundException();
        }

        return ToDto(branch);
    }

    public async Task<BranchDto> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken)
    {
        EnsureTimeZoneExists(request.TimeZoneId);

        // OrganizationId comes from the principal; the request schema has no such field (SEC-003).
        var branch = Branch.Create(
            branchContext.EffectiveOrganizationId,
            request.Name,
            request.Code,
            request.Address,
            request.TimeZoneId,
            clock.UtcNow);

        dbContext.Branches.Add(branch);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.BranchCreate,
            EntityType = nameof(Branch),
            EntityId = branch.Id,
            Metadata = JsonSerializer.SerializeToDocument(new { code = branch.Code, name = branch.Name }),
        });

        await SaveDetectingDuplicateAsync(cancellationToken);

        return ToDto(branch);
    }

    public async Task<BranchDto> UpdateAsync(Guid branchId, UpdateBranchRequest request, CancellationToken cancellationToken)
    {
        EnsureTimeZoneExists(request.TimeZoneId);

        var branch = await FindInOrganizationAsync(branchId, cancellationToken, tracked: true);
        dbContext.Expect(branch, request.ConcurrencyToken);

        var previousTimeZone = branch.TimeZoneId;
        branch.Update(request.Name, request.Code, request.Address, request.TimeZoneId, clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.BranchUpdate,
            EntityType = nameof(Branch),
            EntityId = branch.Id,
            Metadata = JsonSerializer.SerializeToDocument(new { code = branch.Code, name = branch.Name }),
        });

        // BRN-029: a time-zone change is recorded separately with both values. It does not touch
        // existing CategorySettings and does not recompute any deadline already issued — the interface
        // warns about that, and the audit trail is what explains it afterwards.
        if (!string.Equals(previousTimeZone, branch.TimeZoneId, StringComparison.Ordinal))
        {
            auditWriter.Write(new AuditEntry
            {
                Action = AuditActions.BranchTimeZoneChange,
                EntityType = nameof(Branch),
                EntityId = branch.Id,
                Metadata = JsonSerializer.SerializeToDocument(new
                {
                    previousTimeZoneId = previousTimeZone,
                    newTimeZoneId = branch.TimeZoneId,
                }),
            });
        }

        await SaveDetectingDuplicateAsync(cancellationToken, branch);

        return ToDto(branch);
    }

    public async Task<BranchDto> ActivateAsync(Guid branchId, BranchActionRequest request, CancellationToken cancellationToken)
    {
        var branch = await FindInOrganizationAsync(branchId, cancellationToken, tracked: true);
        dbContext.Expect(branch, request.ConcurrencyToken);

        branch.Activate(clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.BranchActivate,
            EntityType = nameof(Branch),
            EntityId = branch.Id,
        });

        await NotifyBranchStateChangeAsync(branch, NotificationEventTypes.BranchActivated, cancellationToken);
        await dbContext.SaveWithConcurrencyCheckAsync(branch, cancellationToken);

        return ToDto(branch);
    }

    public async Task<BranchDto> DeactivateAsync(Guid branchId, DeactivateBranchRequest request, CancellationToken cancellationToken)
    {
        var branch = await FindInOrganizationAsync(branchId, cancellationToken, tracked: true);
        dbContext.Expect(branch, request.ConcurrencyToken);

        // BRN-030: active users are not deactivated with the branch, so the operator has to say
        // explicitly that they know the branch is still staffed.
        if (!request.ConfirmActiveUsers)
        {
            var hasActiveUsers = await dbContext.Users
                .IgnoreQueryFilters()
                .AnyAsync(
                    u => u.BranchId == branch.Id && u.OrganizationId == branch.OrganizationId && u.IsActive,
                    cancellationToken);

            if (hasActiveUsers)
            {
                throw new ConflictException(
                    ErrorCodes.BranchHasActiveUsers,
                    "В филиале есть активные пользователи. Подтвердите деактивацию полем confirmActiveUsers.");
            }
        }

        // Throws HEAD_OFFICE_DEACTIVATION_FORBIDDEN while this branch still holds the flag: an
        // organization without a head office is an invalid state (BRN-034).
        branch.Deactivate(clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.BranchDeactivate,
            EntityType = nameof(Branch),
            EntityId = branch.Id,
            Metadata = JsonSerializer.SerializeToDocument(new { confirmedActiveUsers = request.ConfirmActiveUsers }),
        });

        await NotifyBranchStateChangeAsync(branch, NotificationEventTypes.BranchDeactivated, cancellationToken);
        await dbContext.SaveWithConcurrencyCheckAsync(branch, cancellationToken);

        return ToDto(branch);
    }

    public Task<BranchDto> MakeHeadOfficeAsync(Guid branchId, BranchActionRequest request, CancellationToken cancellationToken)
    {
        // The connection uses EnableRetryOnFailure, and a retrying strategy refuses a transaction it
        // does not own — it has to be able to replay the whole unit.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(() => MakeHeadOfficeCoreAsync(branchId, request, cancellationToken));
    }

    private async Task<BranchDto> MakeHeadOfficeCoreAsync(
        Guid branchId,
        BranchActionRequest request,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Serialises concurrent transfers on the organization row. Without it both callers would read
        // "one head office exists", both would clear it, and the partial unique index would decide the
        // winner by chance while the loser had already committed a clear (BRN-046, TEST-TEN-033).
        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT id FROM organizations WHERE id = {branchContext.EffectiveOrganizationId} FOR UPDATE",
            cancellationToken);

        var target = await FindInOrganizationAsync(branchId, cancellationToken, tracked: true);
        dbContext.Expect(target, request.ConcurrencyToken);

        var previous = await dbContext.Branches
            .FirstOrDefaultAsync(
                b => b.OrganizationId == branchContext.EffectiveOrganizationId && b.IsHeadOffice,
                cancellationToken);

        if (previous?.Id == target.Id)
        {
            throw new ConflictException(
                ErrorCodes.HeadOfficeRequired,
                "Филиал уже является главным офисом.");
        }

        // Clear first, then set. The reverse order would momentarily leave two rows with the flag and
        // violate ux_branches_single_head_office inside the transaction (BRN-035).
        previous?.ClearHeadOffice(clock.UtcNow);

        // Refuses an inactive branch: the organization must never be left with an unusable head
        // office (BRN-047).
        target.MarkAsHeadOffice(clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.BranchMakeHeadOffice,
            EntityType = nameof(Branch),
            EntityId = target.Id,
            Metadata = JsonSerializer.SerializeToDocument(new
            {
                previousHeadOfficeId = previous?.Id,
                newHeadOfficeId = target.Id,
            }),
        });

        await dbContext.SaveWithConcurrencyCheckAsync(target, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ToDto(target);
    }

    // -----------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------

    private async Task<Branch> FindInOrganizationAsync(
        Guid branchId,
        CancellationToken cancellationToken,
        bool tracked = false)
    {
        var source = tracked ? dbContext.Branches : dbContext.Branches.AsNoTracking();

        // The organization is pinned explicitly. A branch of another organization is indistinguishable
        // from one that does not exist — no code confirms its existence (TEN-006, TEN-007).
        return await source.FirstOrDefaultAsync(
                   b => b.Id == branchId && b.OrganizationId == branchContext.EffectiveOrganizationId,
                   cancellationToken)
               ?? throw new NotFoundException();
    }

    private void EnsureTimeZoneExists(string timeZoneId)
    {
        if (!timeZones.Exists(timeZoneId))
        {
            throw new ValidationAppException(
                "timeZoneId",
                "Часовой пояс должен быть идентификатором IANA, присутствующим в базе tzdata (например Asia/Dushanbe).");
        }
    }

    private static IQueryable<Branch> ApplySort(IQueryable<Branch> source, BranchListQuery query)
    {
        var descending = string.Equals(query.Order, "desc", StringComparison.OrdinalIgnoreCase);
        var sort = query.Sort;

        // API-004: sorting is allowed only by whitelisted fields; anything else is a 400 rather than
        // silently ignored, so a client cannot probe the column set.
        if (!string.IsNullOrWhiteSpace(sort)
            && !SortableColumns.Contains(sort, StringComparer.OrdinalIgnoreCase))
        {
            throw new ValidationAppException(
                "sort",
                $"Сортировка допустима только по полям: {string.Join(", ", SortableColumns)}.");
        }

        return sort?.ToLowerInvariant() switch
        {
            "code" => descending ? source.OrderByDescending(b => b.Code) : source.OrderBy(b => b.Code),
            "createdat" => descending ? source.OrderByDescending(b => b.CreatedAt) : source.OrderBy(b => b.CreatedAt),
            _ => descending ? source.OrderByDescending(b => b.Name) : source.OrderBy(b => b.Name),
        };
    }

    /// <summary>
    /// Notifies the people affected by a branch going up or down (Приложение E).
    /// </summary>
    /// <remarks>
    /// Deactivation reaches everyone in the branch — they are about to lose the ability to write.
    /// Activation goes to the branch administrators and to the organization administrators, who
    /// asked for it; telling every mentor that a branch they may not belong to came back adds noise
    /// without information.
    /// </remarks>
    private async Task NotifyBranchStateChangeAsync(Branch branch, string eventType, CancellationToken cancellationToken)
    {
        var recipients = await dbContext.Users
            .IgnoreQueryFilters()
            .Where(u => u.OrganizationId == branch.OrganizationId && u.IsActive)
            .Where(u => eventType == NotificationEventTypes.BranchDeactivated
                ? u.BranchId == branch.Id
                : u.BranchId == branch.Id || (u.AdminScope == AdminScope.Organization))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        var localDate = clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");

        foreach (var recipientId in recipients)
        {
            outboxWriter.EnqueueSystem(
                new OutboxEntry
                {
                    RecipientUserId = recipientId,
                    EventType = eventType,
                    Channel = NotificationChannel.Email,

                    // The key names the branch, the recipient and the day, so a repeated activation
                    // within one day does not send twice (Приложение E).
                    DeduplicationKey = $"{eventType}:{branch.Id:N}:{recipientId:N}:{localDate}",
                    Payload = JsonSerializer.SerializeToDocument(new
                    {
                        branchName = branch.Name,
                        branchCode = branch.Code,
                    }),
                },
                branch.OrganizationId,
                branch.Id);
        }
    }

    /// <summary>
    /// Turns a unique-index violation into 409 <c>BRANCH_ALREADY_EXISTS</c> (<c>BRN-043</c>).
    /// </summary>
    /// <remarks>
    /// The uniqueness of <c>code</c> and <c>normalized_name</c> is guaranteed by the index, not by a
    /// prior lookup: two concurrent creations would both pass a service check and one would still have
    /// to be rejected. Catching the violation is therefore the primary path, not a fallback
    /// (scenario 17 of Приложение K).
    /// </remarks>
    private async Task SaveDetectingDuplicateAsync(CancellationToken cancellationToken, Branch? tracked = null)
    {
        try
        {
            if (tracked is not null)
            {
                await dbContext.SaveWithConcurrencyCheckAsync(tracked, cancellationToken);
            }
            else
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                                                  {
                                                      SqlState: PostgresErrorCodes.UniqueViolation,
                                                  } postgres)
        {
            if (postgres.ConstraintName is "ux_branches_organization_code"
                or "ux_branches_organization_normalized_name")
            {
                throw new ConflictException(
                    ErrorCodes.BranchAlreadyExists,
                    "Филиал с таким кодом или названием уже существует в организации.");
            }

            throw;
        }
    }

    private BranchDto ToDto(Branch branch) => new(
        branch.Id,
        branch.OrganizationId,
        branch.Name,
        branch.Code,
        branch.Address,
        branch.TimeZoneId,
        branch.IsHeadOffice,
        branch.IsActive,
        branch.CreatedAt,
        branch.UpdatedAt,
        dbContext.Read(branch));

    private static BranchSummaryDto ToSummary(Branch branch) =>
        new(branch.Id, branch.Name, branch.Code, branch.IsHeadOffice);
}
