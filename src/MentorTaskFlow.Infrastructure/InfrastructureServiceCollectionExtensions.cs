using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Infrastructure.Common;
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

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is required (DEPLOY-014/DEPLOY-015).");

        var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? new DatabaseOptions();

        services.AddDbContext<MentorTaskFlowDbContext>(options =>
        {
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

        return services;
    }
}
