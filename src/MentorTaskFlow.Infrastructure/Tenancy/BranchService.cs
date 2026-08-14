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
using MentorTaskFlow.Infrastructure.Persistence.Configurations;
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

        // xmin is projected by the query, not read from the change tracker. These rows are not
        // tracked, and Entry() on a detached entity starts tracking afresh with default shadow
        // values — the token would encode 0 and the client's very first write would be refused as a
        // conflict.
        var rows = await source
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new BranchRow(b, EF.Property<uint>(b, ConcurrencyTokenExtensions.PropertyName)))
            .ToListAsync(cancellationToken);

        var items = rows.Select(row => ToDto(row.Branch, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin))).ToList();

        return new PagedResult<BranchDto>(items, page, pageSize, totalCount);
    }

    public async Task<object> GetAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var user = currentUser.Current ?? throw new UnauthorizedException();

        var row = await dbContext.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId && b.OrganizationId == branchContext.EffectiveOrganizationId)
            .Select(b => new BranchRow(b, EF.Property<uint>(b, ConcurrencyTokenExtensions.PropertyName)))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        // A Lead or Mentor may read only their own branch, and only the minimal projection: address
        // and time zone play no part in their scenarios (BRN-009).
        if (user.Role is UserRole.Lead or UserRole.Mentor)
        {
            return row.Branch.Id == user.BranchId
                ? ToSummary(row.Branch)
                : throw new NotFoundException();
        }

        // A Branch Admin has GET on their own branch and nothing else. Any other identifier answers
        // 404, identical to a branch that does not exist (BRN-008, TEN-006).
        if (user is { Role: UserRole.Admin, AdminScope: AdminScope.Branch } && row.Branch.Id != user.BranchId)
        {
            throw new NotFoundException();
        }

        return ToDto(row.Branch, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin));
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

        return ToDto(branch, dbContext.Read(branch));
    }

    public async Task<BranchDto> UpdateAsync(Guid branchId, UpdateBranchRequest request, CancellationToken cancellationToken)
    {
        EnsureTimeZoneExists(request.TimeZoneId);

        var branch = await FindInOrganizationAsync(branchId, cancellationToken);
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

        return ToDto(branch, dbContext.Read(branch));
    }

    public async Task<BranchDto> ActivateAsync(Guid branchId, BranchActionRequest request, CancellationToken cancellationToken)
    {
        var branch = await FindInOrganizationAsync(branchId, cancellationToken);
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

        return ToDto(branch, dbContext.Read(branch));
    }

    public async Task<BranchDto> DeactivateAsync(Guid branchId, DeactivateBranchRequest request, CancellationToken cancellationToken)
    {
        var branch = await FindInOrganizationAsync(branchId, cancellationToken);
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

        return ToDto(branch, dbContext.Read(branch));
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

        var previousHeadOfficeId = await dbContext.Branches
            .AsNoTracking()
            .Where(b => b.OrganizationId == branchContext.EffectiveOrganizationId && b.IsHeadOffice)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (previousHeadOfficeId == branchId)
        {
            throw new ConflictException(
                ErrorCodes.HeadOfficeRequired,
                "Филиал уже является главным офисом.");
        }

        var now = clock.UtcNow;

        // Clear first, then set, as two statements in that order — exactly the sketch of BRN-035.
        //
        // The clear is raw SQL rather than a tracked entity on purpose: EF Core decides the order of
        // its UPDATE statements for two rows of the same table, and it is under no obligation to
        // preserve the order the entities were modified in. When it emitted the SET before the CLEAR,
        // ux_branches_single_head_office fired on a perfectly legitimate transfer. Issuing the
        // statements explicitly removes the guesswork.
        await dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE branches
             SET is_head_office = false, updated_at = {now}
             WHERE organization_id = {branchContext.EffectiveOrganizationId} AND is_head_office = true
             """,
            cancellationToken);

        var target = await FindInOrganizationAsync(branchId, cancellationToken);
        dbContext.Expect(target, request.ConcurrencyToken);

        // Refuses an inactive branch: the organization must never be left with an unusable head
        // office (BRN-047).
        target.MarkAsHeadOffice(now);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.BranchMakeHeadOffice,
            EntityType = nameof(Branch),
            EntityId = target.Id,
            Metadata = JsonSerializer.SerializeToDocument(new
            {
                previousHeadOfficeId,
                newHeadOfficeId = target.Id,
            }),
        });

        try
        {
            await dbContext.SaveWithConcurrencyCheckAsync(target, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsHeadOfficeViolation(exception))
        {
            // The row lock narrows the window but is not the guarantee: ux_branches_single_head_office
            // is (BRN-021), and it is what fires when two transfers still overlap. Translating the
            // violation gives the loser the documented 409 CONCURRENCY_CONFLICT of BRN-046 instead of
            // a 500, so the client knows to reload and retry rather than treating it as a server fault.
            await transaction.RollbackAsync(cancellationToken);

            throw new ConflictException(
                ErrorCodes.ConcurrencyConflict,
                "Главный офис был изменён другим администратором. Перезагрузите данные и повторите операцию.");
        }

        await transaction.CommitAsync(cancellationToken);

        return ToDto(target, dbContext.Read(target));
    }

    // -----------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------

    /// <summary>Loads a <b>tracked</b> branch for a write path.</summary>
    /// <remarks>
    /// The organization is pinned explicitly. A branch of another organization is indistinguishable
    /// from one that does not exist — no code confirms its existence (<c>TEN-006</c>, <c>TEN-007</c>).
    /// </remarks>
    private async Task<Branch> FindInOrganizationAsync(Guid branchId, CancellationToken cancellationToken) =>
        await dbContext.Branches.FirstOrDefaultAsync(
                b => b.Id == branchId && b.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

    /// <summary>Carries the shadow <c>xmin</c> out of a no-tracking query alongside its entity.</summary>
    private sealed record BranchRow(Branch Branch, uint Xmin);

    private static bool IsHeadOfficeViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_branches_single_head_office",
        };

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
            await outboxWriter.EnqueueSystemAsync(
                new OutboxEntry
                {
                    RecipientUserId = recipientId,
                    EventType = eventType,
                    EntityId = branch.Id,

                    // The recipient and the day, so a repeated activation within one day does not send
                    // twice (Приложение E). The branch is already in the key's scope prefix.
                    Discriminator = $"{recipientId:N}:{localDate}",
                    Payload = JsonSerializer.SerializeToDocument(new
                    {
                        branchName = branch.Name,
                        branchCode = branch.Code,
                    }),
                },
                branch.OrganizationId,
                branch.Id,
                cancellationToken);
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

    private static BranchDto ToDto(Branch branch, string concurrencyToken) => new(
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
        concurrencyToken);

    private static BranchSummaryDto ToSummary(Branch branch) =>
        new(branch.Id, branch.Name, branch.Code, branch.IsHeadOffice);
}
