using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MentorTaskFlow.IntegrationTests.Persistence;

/// <summary>
/// A real PostgreSQL 16 instance with the schema applied, shared by the isolation tests.
/// </summary>
/// <remarks>
/// <c>TEN-099</c> requires these tests to run against a real database rather than an in-memory
/// provider: their whole purpose is to prove that isolation survives even when the application code
/// is wrong or bypassed, and an in-memory provider enforces no constraint at all.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("mentortaskflow_tests")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = CreateContext(suppressTenantFilter: true);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Builds a context. <paramref name="suppressTenantFilter"/> mirrors the registered system-task
    /// exception of <c>SEC-031</c> and is used only by arrange steps; the assertions themselves run
    /// under a real tenant scope.
    /// </summary>
    public MentorTaskFlowDbContext CreateContext(
        bool suppressTenantFilter = false,
        Guid? organizationId = null,
        Guid? branchId = null)
    {
        var state = new TenantFilterState();

        if (suppressTenantFilter)
        {
            state.Suppress();
        }
        else if (organizationId is { } org)
        {
            state.SetScope(org, branchId);
        }

        var options = new DbContextOptionsBuilder<MentorTaskFlowDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new MentorTaskFlowDbContext(options, state);
    }

    /// <summary>
    /// Opens a raw connection so a test can issue SQL that bypasses the application entirely — the
    /// technique <c>TEST-TEN-040</c> mandates.
    /// </summary>
    public async Task<NpgsqlConnection> OpenRawConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Removes every row so each test starts from a known state.</summary>
    public async Task ResetAsync()
    {
        await using var connection = await OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            TRUNCATE TABLE user_branch_history, user_category_history, category_settings,
                           users, categories, branches, organizations
            RESTART IDENTITY CASCADE;
            """;
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
