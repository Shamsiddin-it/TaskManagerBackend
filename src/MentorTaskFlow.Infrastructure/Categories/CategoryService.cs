using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Categories;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Tenancy;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Persistence;
using MentorTaskFlow.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MentorTaskFlow.Infrastructure.Categories;

/// <inheritdoc />
public sealed class CategoryService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    ITenantStateGuard tenantState,
    IAuditWriter auditWriter,
    ITimeZoneCatalog timeZones,
    IClock clock) : ICategoryService
{
    private static readonly string[] SortableColumns = ["name", "createdAt"];

    public async Task<PagedResult<CategoryDto>> ListAsync(CategoryListQuery query, CancellationToken cancellationToken)
    {
        var user = currentUser.Current ?? throw new UnauthorizedException();

        var page = Math.Max(query.Page, PaginationLimits.DefaultPage);
        var pageSize = Math.Clamp(query.PageSize, PaginationLimits.MinPageSize, PaginationLimits.MaxPageSize);

        var source = dbContext.Categories
            .AsNoTracking()
            .Where(c => c.OrganizationId == branchContext.EffectiveOrganizationId);

        // A Lead or Mentor sees exactly one category — their own (CAT-007). The narrowing is explicit
        // here rather than left to the query filter, which is scoped to the branch and would still
        // show them the siblings inside it.
        if (user.Role is UserRole.Lead or UserRole.Mentor)
        {
            source = source.Where(c => c.Id == user.CategoryId);
        }
        else if (branchContext.EffectiveBranchId is { } branchId)
        {
            source = source.Where(c => c.BranchId == branchId);
        }

        if (query.IsActive is { } isActive)
        {
            source = source.Where(c => c.IsActive == isActive);
        }

        source = ApplySort(source, query);

        var totalCount = await source.CountAsync(cancellationToken);

        var rows = await source
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CategoryRow(c, EF.Property<uint>(c, ConcurrencyTokenExtensions.PropertyName)))
            .ToListAsync(cancellationToken);

        // TEN-073: in the all-branches context every row must name its branch. Two categories may
        // share a name and be told apart only by it, so a row without one is a contract defect.
        var branches = branchContext.IsAllBranchesReadContext
            ? await LoadBranchSummariesAsync(rows.Select(r => r.Category.BranchId), cancellationToken)
            : [];

        var items = rows
            .Select(row => ToDto(row, branches.GetValueOrDefault(row.Category.BranchId)))
            .ToList();

        return new PagedResult<CategoryDto>(items, page, pageSize, totalCount);
    }

    public async Task<CategoryDto> GetAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var row = await FindReadableAsync(categoryId, cancellationToken);

        return ToDto(row, null);
    }

    public async Task<CategoryDto> CreateAsync(CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        // CAT-025: a Branch Admin takes the branch from their claim; an Organization Admin must have
        // chosen one, and RequireBranchForMutation answers 400 BRANCH_CONTEXT_REQUIRED otherwise. No
        // endpoint changes more than one branch per request, so there is no default to assume.
        var branchId = branchContext.RequireBranchForMutation();

        await tenantState.EnsureWritableAsync(branchId, categoryId: null, cancellationToken);

        var now = clock.UtcNow;

        var category = Category.Create(
            branchContext.EffectiveOrganizationId,
            branchId,
            request.Name,
            request.Description,
            now);

        // CAT-023: the settings inherit the branch time zone. A later change to Branch.TimeZoneId does
        // not propagate here — shifting the deadline arithmetic of live categories retroactively is
        // what BRN-029 forbids.
        var branchTimeZone = await dbContext.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => b.TimeZoneId)
            .FirstAsync(cancellationToken);

        // CAT-014: one transaction, or a category without settings could exist. EF Core writes both
        // inserts in a single SaveChanges, which is that transaction.
        dbContext.Categories.Add(category);
        dbContext.CategorySettings.Add(CategorySettings.CreateDefault(category, branchTimeZone, now));

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.CategoryCreate,
            EntityType = nameof(Category),
            EntityId = category.Id,
            CategoryId = category.Id,
            Metadata = JsonSerializer.SerializeToDocument(new { name = category.Name }),
        });

        await SaveDetectingDuplicateAsync(cancellationToken);

        return ToDto(category);
    }

    public async Task<CategoryDto> UpdateAsync(Guid categoryId, UpdateCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await FindWritableAsync(categoryId, cancellationToken);
        dbContext.Expect(category, request.ConcurrencyToken);

        category.Update(request.Name, request.Description, now: clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.CategoryUpdate,
            EntityType = nameof(Category),
            EntityId = category.Id,
            CategoryId = category.Id,
            Metadata = JsonSerializer.SerializeToDocument(new { name = category.Name }),
        });

        await SaveDetectingDuplicateAsync(cancellationToken, category);

        return ToDto(category);
    }

    public async Task<CategoryDto> ActivateAsync(Guid categoryId, CategoryActionRequest request, CancellationToken cancellationToken)
    {
        // The category itself is deactivated, so only the branch above it is checked — requiring an
        // active category here would make activation impossible.
        var category = await FindTrackedAsync(categoryId, cancellationToken);
        await tenantState.EnsureWritableAsync(category.BranchId, categoryId: null, cancellationToken);

        dbContext.Expect(category, request.ConcurrencyToken);
        category.Activate(clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.CategoryActivate,
            EntityType = nameof(Category),
            EntityId = category.Id,
            CategoryId = category.Id,
        });

        await dbContext.SaveWithConcurrencyCheckAsync(category, cancellationToken);

        return ToDto(category);
    }

    public async Task<CategoryDto> DeactivateAsync(Guid categoryId, DeactivateCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await FindWritableAsync(categoryId, cancellationToken);
        dbContext.Expect(category, request.ConcurrencyToken);

        // CAT-003: users are not deactivated along with the category, so the operator must say
        // explicitly that they know it is still staffed.
        if (!request.ConfirmActiveUsers)
        {
            var hasActiveUsers = await dbContext.Users
                .IgnoreQueryFilters()
                .AnyAsync(
                    u => u.CategoryId == category.Id
                         && u.OrganizationId == category.OrganizationId
                         && u.IsActive,
                    cancellationToken);

            if (hasActiveUsers)
            {
                throw new ConflictException(
                    ErrorCodes.CategoryHasActiveUsers,
                    "В категории есть активные пользователи. Подтвердите деактивацию полем confirmActiveUsers.");
            }
        }

        category.Deactivate(clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.CategoryDeactivate,
            EntityType = nameof(Category),
            EntityId = category.Id,
            CategoryId = category.Id,
            Metadata = JsonSerializer.SerializeToDocument(new { confirmedActiveUsers = request.ConfirmActiveUsers }),
        });

        await dbContext.SaveWithConcurrencyCheckAsync(category, cancellationToken);

        return ToDto(category);
    }

    public async Task<CategorySettingsDto> GetSettingsAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        // Reuses the read check, so a Lead or Mentor of another category answers 404 rather than
        // leaking the existence of the settings row (CAT-006).
        await FindReadableAsync(categoryId, cancellationToken);

        var row = await dbContext.CategorySettings
            .AsNoTracking()
            .Where(s => s.CategoryId == categoryId)
            .Select(s => new SettingsRow(s, EF.Property<uint>(s, ConcurrencyTokenExtensions.PropertyName)))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        return ToDto(row);
    }

    public async Task<CategorySettingsDto> UpdateSettingsAsync(
        Guid categoryId,
        UpdateCategorySettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (!timeZones.Exists(request.TimeZoneId))
        {
            throw new ValidationAppException(
                "timeZoneId",
                "Часовой пояс должен быть идентификатором IANA, присутствующим в базе tzdata (например Asia/Dushanbe).");
        }

        var category = await FindWritableAsync(categoryId, cancellationToken);

        var settings = await dbContext.CategorySettings
            .FirstOrDefaultAsync(s => s.CategoryId == category.Id, cancellationToken)
            ?? throw new NotFoundException();

        dbContext.Expect(settings, request.ConcurrencyToken);

        var previousTimeZone = settings.TimeZoneId;

        settings.Update(
            request.TimeZoneId,
            request.DefaultAssignmentDueDays,
            request.DefaultDueTimeLocal,
            request.DeadlineReminderHours,
            request.AllowLateSubmission,
            clock.UtcNow);

        auditWriter.Write(new AuditEntry
        {
            Action = AuditActions.CategorySettingsUpdate,
            EntityType = nameof(CategorySettings),
            EntityId = category.Id,
            CategoryId = category.Id,

            // CAT-005: the time zone is recorded with both values because it decides every future
            // deadline and the scheduler's firing time. Without the old value an incident review
            // cannot explain why deadlines moved.
            Metadata = JsonSerializer.SerializeToDocument(new
            {
                previousTimeZoneId = previousTimeZone,
                newTimeZoneId = settings.TimeZoneId,
                defaultAssignmentDueDays = settings.DefaultAssignmentDueDays,
                allowLateSubmission = settings.AllowLateSubmission,
            }),
        });

        await dbContext.SaveWithConcurrencyCheckAsync(settings, cancellationToken);

        return ToDto(settings);
    }

    // -----------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------

    /// <summary>Loads a category the caller may read, applying levels 1–4 of TZ 9.1.</summary>
    private async Task<CategoryRow> FindReadableAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var user = currentUser.Current ?? throw new UnauthorizedException();

        var row = await dbContext.Categories
            .AsNoTracking()
            .Where(c => c.Id == categoryId && c.OrganizationId == branchContext.EffectiveOrganizationId)
            .Select(c => new CategoryRow(c, EF.Property<uint>(c, ConcurrencyTokenExtensions.PropertyName)))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        // Every rejection below is 404, never 403: a category of another branch or another category
        // of the same branch must be indistinguishable from one that does not exist (TEN-006).
        var visible = user switch
        {
            { Role: UserRole.Lead or UserRole.Mentor } => row.Category.Id == user.CategoryId,
            { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => row.Category.BranchId == user.BranchId,
            _ => true,
        };

        return visible ? row : throw new NotFoundException();
    }

    /// <summary>Loads a tracked category and verifies the whole chain above it is active.</summary>
    private async Task<Category> FindWritableAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await FindTrackedAsync(categoryId, cancellationToken);

        await tenantState.EnsureWritableAsync(category.BranchId, category.Id, cancellationToken);

        return category;
    }

    private async Task<Category> FindTrackedAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var user = currentUser.Current ?? throw new UnauthorizedException();

        var category = await dbContext.Categories.FirstOrDefaultAsync(
                c => c.Id == categoryId && c.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        // A Branch Admin may write only inside their own branch; anything else is invisible to them.
        if (user is { Role: UserRole.Admin, AdminScope: AdminScope.Branch } && category.BranchId != user.BranchId)
        {
            throw new NotFoundException();
        }

        return category;
    }

    private async Task<Dictionary<Guid, BranchSummaryDto>> LoadBranchSummariesAsync(
        IEnumerable<Guid> branchIds,
        CancellationToken cancellationToken)
    {
        var ids = branchIds.Distinct().ToArray();

        return await dbContext.Branches
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => new BranchSummaryDto(b.Id, b.Name, b.Code, b.IsHeadOffice))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
    }

    private static IQueryable<Category> ApplySort(IQueryable<Category> source, CategoryListQuery query)
    {
        var descending = string.Equals(query.Order, "desc", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(query.Sort)
            && !SortableColumns.Contains(query.Sort, StringComparer.OrdinalIgnoreCase))
        {
            throw new ValidationAppException(
                "sort",
                $"Сортировка допустима только по полям: {string.Join(", ", SortableColumns)}.");
        }

        return query.Sort?.ToLowerInvariant() switch
        {
            "createdat" => descending ? source.OrderByDescending(c => c.CreatedAt) : source.OrderBy(c => c.CreatedAt),
            _ => descending ? source.OrderByDescending(c => c.Name) : source.OrderBy(c => c.Name),
        };
    }

    /// <summary>
    /// Turns a duplicate name into 409 <c>RESOURCE_ALREADY_EXISTS</c>.
    /// </summary>
    /// <remarks>
    /// Uniqueness is per branch, not global: `C#` may exist in the head office and in Khujand at the
    /// same time as two different entities (<c>CAT-021</c>). The index decides, not a prior lookup —
    /// two concurrent creations would both pass a service check.
    /// </remarks>
    private async Task SaveDetectingDuplicateAsync(CancellationToken cancellationToken, Category? tracked = null)
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
                                                      ConstraintName: "ux_categories_branch_normalized_name",
                                                  })
        {
            throw new ConflictException(
                ErrorCodes.ResourceAlreadyExists,
                "Категория с таким названием уже существует в этом филиале.");
        }
    }

    /// <summary>Builds the response for a <b>tracked</b> entity, whose token comes from the change tracker.</summary>
    private CategoryDto ToDto(Category category) => ToDto(category, dbContext.Read(category), branch: null);

    /// <summary>Builds the response for a no-tracking row, whose token was projected by the query.</summary>
    private static CategoryDto ToDto(CategoryRow row, BranchSummaryDto? branch) =>
        ToDto(row.Category, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin), branch);

    private static CategoryDto ToDto(Category category, string concurrencyToken, BranchSummaryDto? branch) => new(
        category.Id,
        category.OrganizationId,
        category.BranchId,
        category.Name,
        category.Description,
        category.IsActive,
        category.CreatedAt,
        category.UpdatedAt,
        concurrencyToken,
        branch);

    private CategorySettingsDto ToDto(CategorySettings settings) =>
        ToDto(settings, dbContext.Read(settings));

    private static CategorySettingsDto ToDto(SettingsRow row) =>
        ToDto(row.Settings, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin));

    private static CategorySettingsDto ToDto(CategorySettings settings, string concurrencyToken) => new(
        settings.CategoryId,
        settings.TimeZoneId,
        settings.DefaultAssignmentDueDays,
        settings.DefaultDueTimeLocal,
        settings.DeadlineReminderHours,
        settings.AllowLateSubmission,
        settings.UpdatedAt,
        concurrencyToken);

    /// <summary>Carries the shadow <c>xmin</c> out of a no-tracking query alongside its entity.</summary>
    private sealed record CategoryRow(Category Category, uint Xmin);

    private sealed record SettingsRow(CategorySettings Settings, uint Xmin);
}
