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
/// reaching the endpoint. It also keeps the auth cookies non-<c>Secure</c>, which is what lets the
/// test client retain them over plain HTTP.
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

    /// <summary>
    /// 60 characters — well over the 256-bit floor of <c>AUTH-001</c> — and free of the placeholder
    /// words <c>AuthOptionsValidator</c> refuses to start on.
    /// </summary>
    public const string TestSigningKey = "mtf-integration-signing-key-0123456789-abcdefghijklmnopqrst";

    public const string AllowedOrigin = "https://app.mentortaskflow.test";

    /// <summary>Points the API at a live database when a test needs real persistence.</summary>
    public string? ConnectionStringOverride { get; init; }

    /// <summary>
    /// Points the API at a live MinIO when a test needs real storage.
    /// </summary>
    /// <remarks>
    /// Unset, the storage options are still filled with a syntactically valid but unreachable
    /// endpoint. <c>StorageOptions</c> is validated at startup, so leaving the credentials blank would
    /// stop every test in the suite from booting — including the ones that never touch a file.
    /// </remarks>
    public string? StorageEndpointOverride { get; init; }

    /// <summary>
    /// Turns the Telegram feature on for a test.
    /// </summary>
    /// <remarks>
    /// Off by default, matching a deployment without a bot: with the flag down the bind endpoints
    /// answer 404, which several tests assert directly (4.1).
    /// </remarks>
    public bool TelegramEnabled { get; init; }

    public const string TelegramWebhookSecret = "integration-webhook-secret-0123456789";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ConnectionStringOverride ?? UnusedConnectionString,
                ["Database:MigrateOnStartup"] = "false",
                ["Cors:AllowedOrigins:0"] = AllowedOrigin,
                ["Auth:JwtSigningKey"] = TestSigningKey,
                ["Auth:JwtIssuer"] = "mentortaskflow-tests",
                ["Auth:JwtAudience"] = "mentortaskflow-tests-api",
                ["Auth:AppBaseUrl"] = AllowedOrigin,

                ["Storage:Endpoint"] = StorageEndpointOverride ?? "http://127.0.0.1:1",
                ["Storage:AccessKey"] = Persistence.MinioFixture.AccessKey,
                ["Storage:SecretKey"] = Persistence.MinioFixture.SecretKey,
                ["Storage:Bucket"] = Persistence.MinioFixture.Bucket,

                ["Telegram:Enabled"] = TelegramEnabled ? "true" : "false",
                ["Telegram:BotUsername"] = "mentortaskflow_test_bot",
                ["Telegram:BotToken"] = "test-bot-token",
                ["Telegram:WebhookSecret"] = TelegramWebhookSecret,
            });
        });
    }
}
