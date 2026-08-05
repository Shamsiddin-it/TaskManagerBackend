using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MentorTaskFlow.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time so migrations can be scaffolded without booting the API.
/// </summary>
/// <remarks>
/// Reads <c>MTF_DESIGN_TIME_CONNECTION</c> and falls back to a local development connection string.
/// It never participates in runtime composition and never carries production credentials.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MentorTaskFlowDbContext>
{
    private const string FallbackConnection =
        "Host=localhost;Port=5432;Database=mentortaskflow;Username=postgres;Password=postgres";

    public MentorTaskFlowDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("MTF_DESIGN_TIME_CONNECTION") ?? FallbackConnection;

        var options = new DbContextOptionsBuilder<MentorTaskFlowDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new MentorTaskFlowDbContext(options);
    }
}
