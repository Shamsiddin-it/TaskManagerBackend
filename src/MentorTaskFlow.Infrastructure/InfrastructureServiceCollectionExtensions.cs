using Amazon.Runtime;
using Hangfire;
using Hangfire.PostgreSql;
using Amazon.S3;
using Anthropic;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Security;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Infrastructure.Analytics;
using MentorTaskFlow.Infrastructure.Assignments;
using MentorTaskFlow.Infrastructure.Auditing;
using MentorTaskFlow.Infrastructure.Categories;
using MentorTaskFlow.Infrastructure.Common;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.Infrastructure.Notifications;
using MentorTaskFlow.Infrastructure.Reviews;
using MentorTaskFlow.Infrastructure.Scheduling;
using MentorTaskFlow.Infrastructure.Schedule;
using MentorTaskFlow.Infrastructure.Storage;
using MentorTaskFlow.Infrastructure.Submissions;
using MentorTaskFlow.Infrastructure.Telegram;
using MentorTaskFlow.Infrastructure.Tenancy;
using MentorTaskFlow.Infrastructure.Users;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<DatabaseOptions>>(new DatabaseOptionsValidator(isDevelopment));

        // Resolved from the service provider, not captured here. Reading configuration eagerly during
        // registration takes a snapshot before every source has been added, so a value supplied later
        // — by a test host or a late-bound secret provider — would be silently ignored while the API
        // pointed at the wrong database.
        services.AddDbContext<MentorTaskFlowDbContext>((serviceProvider, options) =>
        {
            var runtimeConfiguration = serviceProvider.GetRequiredService<IConfiguration>();
            var databaseOptions = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            var connectionString = runtimeConfiguration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection is required (DEPLOY-014/DEPLOY-015).");

            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
                npgsql.CommandTimeout(databaseOptions.CommandTimeoutSeconds);
                npgsql.EnableRetryOnFailure(databaseOptions.MaxRetryCount);
            });

            // snake_case for every table, column, index and constraint (DEPLOY-001).
            options.UseSnakeCaseNamingConvention();

            if (isDevelopment)
            {
                options.EnableDetailedErrors();
            }

            // EnableSensitiveDataLogging is never switched on: parameter values would reach the
            // technical log and violate SEC-021 / AUD-022 even in Development.
        });

        services.AddSingleton<IClock, SystemClock>();

        // Scoped: the tenant scope belongs to one request (or to one system-task scope) and must
        // never outlive it. Registered before the DbContext consumes it.
        services.AddScoped<TenantFilterState>();
        services.AddScoped<IBranchScopeValidator, BranchScopeValidator>();

        services.AddMetrics();
        services.AddMemoryCache();

        // OBS-011: labels carry Organization.Slug and Branch.Code, so the resolver is needed before
        // any counter that names a tenant.
        services.AddSingleton<TenantLabelResolver>();
        services.AddSingleton<TenancyMetrics>();
        services.AddSingleton<HttpMetrics>();

        // TEN-096: composition gauges. The snapshot is refreshed in the background and read at scrape
        // time, so a slow database delays a graph rather than timing out the collector.
        services.AddSingleton<TenantGaugeSnapshot>();
        services.AddSingleton<TenantGauges>();
        services.AddHostedService<TenantGaugeRefresher>();

        // The DbContext is the unit of work; the interface exists so controllers can commit without
        // referencing it directly (SEC-031).
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<MentorTaskFlowDbContext>());

        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuditLogReader, AuditLogReader>();

        AddNotifications(services, configuration);

        services.AddSingleton<ITimeZoneCatalog, TimeZoneCatalog>();
        services.AddScoped<ITenantStateGuard, TenantStateGuard>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<IAssignmentService, AssignmentService>();
        services.AddSingleton<IDeadlineCalculator, DeadlineCalculator>();

        services.AddSingleton<AnalyticsMetrics>();
        services.AddScoped<MetricsQuery>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();

        AddIdentity(services, configuration);
        AddStorage(services, configuration);
        AddTelegram(services, configuration);
        AddScheduler(services, configuration);
        AddAi(services, configuration);

        return services;
    }

    /// <summary>AI summaries over the analytics of TZ 21 (TZ 22).</summary>
    /// <remarks>
    /// <para>
    /// The provider is chosen here, once, from configuration. Everything downstream depends on
    /// <c>IAiSummaryProvider</c> and does not know whether a key exists (<c>AI-001</c>) — which is
    /// what makes «no subscription» a deployment decision rather than a code path threaded through
    /// the service.
    /// </para>
    /// <para>
    /// The registration is unconditional: with the feature off the endpoint answers 404 before the
    /// provider is reached, and the readiness probe still reports the optional dependency as degraded
    /// rather than missing (<c>AI-019</c>).
    /// </para>
    /// </remarks>
    private static void AddAi(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<AiMetrics>();
        services.AddSingleton<AiProviderStatus>();
        services.AddScoped<IAiSummaryService, AiSummaryService>();

        var options = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();

        if (!options.IsConfigured)
        {
            // Открытый вопрос 5 — the API key and the budget — is unanswered, so a deployment without
            // one must still boot and still serve every metric. It gets a provider that refuses,
            // not a missing registration that would fail at resolution time.
            services.AddScoped<IAiSummaryProvider, UnconfiguredSummaryProvider>();
            return;
        }

        // Built from the provider rather than a captured snapshot, for the reason the DbContext and
        // the S3 client are: the key arrives from the environment and may be registered after this.
        services.AddSingleton(serviceProvider =>
        {
            var ai = serviceProvider.GetRequiredService<IOptions<AiOptions>>().Value;

            return new AnthropicClient
            {
                ApiKey = ai.ApiKey,

                // Retries belong to AnthropicSummaryProvider, which counts them against the
                // ninety-second budget of AI-003. Left at the SDK default of two they would compound
                // with ours and quietly quadruple the attempts.
                MaxRetries = 0,
                Timeout = TimeSpan.FromSeconds(ai.TimeoutSeconds),
            };
        });

        services.AddScoped<IAiSummaryProvider, AnthropicSummaryProvider>();
    }

    /// <summary>The outbox, its delivery channels and the worker that drains it (TZ 18).</summary>
    private static void AddNotifications(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<NotificationMetrics>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddScoped<INotificationAdminService, NotificationAdminService>();
        services.AddScoped<OutboxDispatcher>();
        services.AddScoped<INotificationSender, SmtpNotificationSender>();

        // The loop is registered unconditionally and stops itself when the process is not the worker:
        // whether background processing belongs here is configuration, not composition (DEPLOY-013).
        services.AddHostedService<OutboxWorker>();
    }

    /// <summary>
    /// Hangfire and the recurring jobs of TZ 20.
    /// </summary>
    /// <remarks>
    /// The Dashboard is not mounted anywhere (<c>TEN-059</c>): it lists job arguments across every
    /// organization, so exposing it to any application user — Organization Admin included — would
    /// hand them another tenant's data. Operational visibility comes from <c>/admin/health</c> and the
    /// notification journal, both bounded by the caller's contour.
    /// </remarks>
    private static void AddScheduler(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SchedulerOptions>()
            .Bind(configuration.GetSection(SchedulerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<SchedulerMetrics>();
        services.AddScoped<MentorSelector>();
        services.AddScoped<AutoGenerationJob>();
        services.AddScoped<OverdueJob>();
        services.AddScoped<DeadlineReminderJob>();
        services.AddScoped<OrphanObjectCleanupJob>();
        services.AddScoped<RetentionJob>();

        var options = configuration.GetSection(SchedulerOptions.SectionName).Get<SchedulerOptions>()
            ?? new SchedulerOptions();

        if (!options.Enabled)
        {
            return;
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        services.AddHangfire(configure => configure
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                postgres => postgres.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions
                {
                    // ADR-002: outside `public`, so an EF migration never diffs — or drops — it.
                    SchemaName = options.Schema,
                    PrepareSchemaIfNecessary = true,
                }));

        if (options.EnableServer)
        {
            services.AddHangfireServer(server => server.WorkerCount = Environment.ProcessorCount);
        }

        services.AddHostedService<SchedulerRegistrar>();
    }

    /// <summary>Telegram binding and the chat delivery channel (TZ 19).</summary>
    private static void AddTelegram(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<ITelegramService, TelegramService>();

        // Registered alongside the SMTP sender: the dispatcher picks by channel, so a Telegram row
        // stops being rescheduled the moment this exists.
        services.AddScoped<INotificationSender, TelegramNotificationSender>();

        services.AddHttpClient(TelegramNotificationSender.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.telegram.org");

            // Bounded so a hung provider cannot hold a worker slot: the row is retried on the next
            // pass instead.
            client.Timeout = TimeSpan.FromSeconds(15);
        });
    }

    /// <summary>Object storage and the file limits of TZ 17.</summary>
    private static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Built from the provider rather than from a captured snapshot, for the same reason the
        // DbContext is: credentials arrive from the environment and may be registered after this call.
        services.AddSingleton<IAmazonS3>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<StorageOptions>>().Value;

            return new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey),
                new AmazonS3Config
                {
                    ServiceURL = options.Endpoint,

                    // MinIO addresses buckets by path; virtual-host style would need DNS per bucket.
                    ForcePathStyle = options.UsePathStyle,
                    UseHttp = !options.UseSsl,

                    // MinIO is not a region-based service, but the SDK requires the field to be set
                    // before it will sign a request at all.
                    AuthenticationRegion = "us-east-1",
                });
        });

        services.AddScoped<IFileStorage, S3FileStorage>();
        services.AddSingleton<UploadedFileInspector>();
        services.AddScoped<ISubmissionService, SubmissionService>();
        services.AddScoped<IReviewService, ReviewService>();
    }

    /// <summary>Authentication services of TZ 16.</summary>
    private static void AddIdentity(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Beyond the data annotations: key strength and placeholder detection. Booting with a weak
        // signing key would issue forgeable tokens, so the failure belongs at startup (DEPLOY-015).
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();

        services.AddOptions<BootstrapOptions>()
            .Bind(configuration.GetSection(BootstrapOptions.SectionName));

        // Stateless and thread-safe, so a singleton. The password catalog in particular parses its
        // embedded resource once instead of on every login.
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecureTokenService, SecureTokenService>();
        services.AddSingleton<ICommonPasswordCatalog, EmbeddedCommonPasswordCatalog>();
        services.AddSingleton<PasswordPolicy>();

        services.AddMemoryCache();

        // Scoped: these depend on the DbContext.
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<ITokenVersionValidator, TokenVersionValidator>();
        services.AddScoped<AuthService>();
        services.AddScoped<IAuthService>(sp => sp.GetRequiredService<AuthService>());
        services.AddScoped<BootstrapProvisioner>();
    }
}
