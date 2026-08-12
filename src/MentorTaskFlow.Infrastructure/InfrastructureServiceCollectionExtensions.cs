using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Security;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Infrastructure.Auditing;
using MentorTaskFlow.Infrastructure.Categories;
using MentorTaskFlow.Infrastructure.Common;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.Infrastructure.Notifications;
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
        services.AddSingleton<TenancyMetrics>();

        // The DbContext is the unit of work; the interface exists so controllers can commit without
        // referencing it directly (SEC-031).
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<MentorTaskFlowDbContext>());

        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuditLogReader, AuditLogReader>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();

        services.AddSingleton<ITimeZoneCatalog, TimeZoneCatalog>();
        services.AddScoped<ITenantStateGuard, TenantStateGuard>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IUserService, UserService>();

        AddIdentity(services, configuration);

        return services;
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
