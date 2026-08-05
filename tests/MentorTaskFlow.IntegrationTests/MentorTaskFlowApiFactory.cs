using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MentorTaskFlow.IntegrationTests;

/// <summary>
/// Boots the real API pipeline in-process.
/// </summary>
/// <remarks>
/// <para>
/// The environment stays <c>Development</c> so the pipeline under test matches the one developers
/// run: HTTPS redirection and HSTS are off, and the test client is not answered with a 307 before
/// reaching the endpoint.
/// </para>
/// <para>
/// <c>Database:MigrateOnStartup</c> is forced off: migrations are the responsibility of the tests
/// that need a schema (they own a Testcontainers PostgreSQL instance from Phase 1), not of the
/// factory. Tests that do not touch the database must not need one to boot.
/// </para>
/// </remarks>
public sealed class MentorTaskFlowApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Points at a port nothing listens on: reachable configuration, unreachable server.</summary>
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=mentortaskflow_tests;Username=none;Password=none;Timeout=1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = UnusedConnectionString,
                ["Database:MigrateOnStartup"] = "false",
                ["Cors:AllowedOrigins:0"] = "https://app.mentortaskflow.test",
            });
        });
    }
}
