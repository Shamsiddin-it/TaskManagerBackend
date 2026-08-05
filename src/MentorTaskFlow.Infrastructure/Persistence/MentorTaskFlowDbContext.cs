using System.Reflection;
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
/// Entities arrive from Phase 1 (Tenancy foundation). Adding an entity here without its
/// <c>organization_id</c>/<c>branch_id</c> columns, composite FK and tenant-leading index is an
/// isolation defect — see DoD item 3a and Приложение M.
/// </para>
/// </remarks>
public class MentorTaskFlowDbContext(DbContextOptions<MentorTaskFlowDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
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
