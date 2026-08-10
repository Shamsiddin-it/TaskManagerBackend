using System.Reflection;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context of the modular monolith.
/// </summary>
/// <remarks>
/// <para>
/// Naming is <c>snake_case</c> and is produced by <c>EFCore.NamingConventions</c>
/// (<c>UseSnakeCaseNamingConvention()</c>, wired in <see cref="InfrastructureServiceCollectionExtensions"/>),
/// never by hand-mapping each column (<c>DEPLOY-001</c>).
/// </para>
/// <para>
/// Adding an entity here without its <c>organization_id</c>/<c>branch_id</c> columns, composite FK,
/// tenant-leading index and query filter is an isolation defect — see DoD item 3a and Приложение M.
/// </para>
/// </remarks>
public class MentorTaskFlowDbContext(
    DbContextOptions<MentorTaskFlowDbContext> options,
    TenantFilterState tenantFilter)
    : DbContext(options), Application.Common.Abstractions.IUnitOfWork
{
    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<CategorySettings> CategorySettings => Set<CategorySettings>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserCategoryHistory> UserCategoryHistory => Set<UserCategoryHistory>();

    public DbSet<UserBranchHistory> UserBranchHistory => Set<UserBranchHistory>();

    // Identity tables carry no tenant columns and therefore no query filter: they are reached only
    // through UserId and take part in no list or analytical query (TEN-009, Приложение M.1).
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<UserSecurityToken> UserSecurityTokens => Set<UserSecurityToken>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<NotificationOutbox> NotificationOutbox => Set<NotificationOutbox>();

    // Referenced by the query-filter expressions below. EF Core re-evaluates these on every query,
    // so one compiled model serves every tenant without leaking a captured value between requests.
    private Guid? FilterOrganizationId => tenantFilter.IsSuppressed ? null : tenantFilter.OrganizationId;

    private Guid? FilterBranchId => tenantFilter.IsSuppressed ? null : tenantFilter.BranchId;

    private bool FilterSuppressed => tenantFilter.IsSuppressed;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>
    /// Level 1 and level 2 of the six-level isolation model (TZ 9.1), applied at the ORM boundary.
    /// </summary>
    /// <remarks>
    /// Each filter is fail-closed: when no scope has been set and suppression was not requested,
    /// <c>FilterOrganizationId</c> is null and nothing matches. Branch narrowing is skipped when
    /// <c>FilterBranchId</c> is null, which is the all-branches read context of an Organization Admin
    /// (<c>TEN-034</c>) — a read-only mode, so nothing is written under a null branch.
    /// </remarks>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Branch>().HasQueryFilter(e =>
            FilterSuppressed || (FilterOrganizationId != null && e.OrganizationId == FilterOrganizationId));

        modelBuilder.Entity<Category>().HasQueryFilter(e =>
            FilterSuppressed
            || (FilterOrganizationId != null
                && e.OrganizationId == FilterOrganizationId
                && (FilterBranchId == null || e.BranchId == FilterBranchId)));

        modelBuilder.Entity<CategorySettings>().HasQueryFilter(e =>
            FilterSuppressed
            || (FilterOrganizationId != null
                && e.OrganizationId == FilterOrganizationId
                && (FilterBranchId == null || e.BranchId == FilterBranchId)));

        // Users are filtered by organization and, when a branch is selected, by branch. An
        // Organization Admin has BranchId NULL, so a branch-narrowed query must still return them —
        // otherwise selecting a branch would hide the organization's own administrators.
        modelBuilder.Entity<User>().HasQueryFilter(e =>
            FilterSuppressed
            || (FilterOrganizationId != null
                && e.OrganizationId == FilterOrganizationId
                && (FilterBranchId == null || e.BranchId == FilterBranchId || e.BranchId == null)));

        modelBuilder.Entity<UserCategoryHistory>().HasQueryFilter(e =>
            FilterSuppressed
            || (FilterOrganizationId != null
                && e.OrganizationId == FilterOrganizationId
                && (FilterBranchId == null || e.BranchId == FilterBranchId)));

        modelBuilder.Entity<UserBranchHistory>().HasQueryFilter(e =>
            FilterSuppressed || (FilterOrganizationId != null && e.OrganizationId == FilterOrganizationId));

        // Organization itself is filtered by primary key: a user never sees that another
        // organization exists (ORG-022).
        modelBuilder.Entity<Organization>().HasQueryFilter(e =>
            FilterSuppressed || (FilterOrganizationId != null && e.Id == FilterOrganizationId));

        // AuditLog and NotificationOutbox use the same shape, and it produces exactly the visibility
        // TEN-040 and TEN-046 require without a special case:
        //   Branch Admin  — FilterBranchId is their branch, so `e.BranchId == FilterBranchId` also
        //                   excludes the organization-level rows they must not see (TEN-049).
        //   Org Admin     — FilterBranchId is null in the all-branches context, so no branch
        //                   narrowing applies and the organization-level rows are visible.
        modelBuilder.Entity<AuditLog>().HasQueryFilter(e =>
            FilterSuppressed
            || (FilterOrganizationId != null
                && e.OrganizationId == FilterOrganizationId
                && (FilterBranchId == null || e.BranchId == FilterBranchId)));

        modelBuilder.Entity<NotificationOutbox>().HasQueryFilter(e =>
            FilterSuppressed
            || (FilterOrganizationId != null
                && e.OrganizationId == FilterOrganizationId
                && (FilterBranchId == null || e.BranchId == FilterBranchId)));
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Every moment in time is timestamptz in UTC; `timestamp without time zone` is forbidden
        // by TZ 11.2. Declaring it as a convention removes the per-property decision.
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<DateTimeOffset?>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<DateOnly>().HaveColumnType("date");
        configurationBuilder.Properties<DateOnly?>().HaveColumnType("date");
        configurationBuilder.Properties<TimeOnly>().HaveColumnType("time");
        configurationBuilder.Properties<TimeOnly?>().HaveColumnType("time");
    }
}
