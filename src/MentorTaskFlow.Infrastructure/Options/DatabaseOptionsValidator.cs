using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Options;

/// <summary>
/// Refuses to start when <see cref="DatabaseOptions.MigrateOnStartup"/> is enabled outside Development.
/// </summary>
/// <remarks>
/// This is a boot-time guard, not a warning: silently migrating from N API replicas is exactly the
/// failure <c>DEPLOY-016</c> forbids, and it only manifests under concurrent deploys.
/// </remarks>
public sealed class DatabaseOptionsValidator(bool isDevelopment) : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        if (options.MigrateOnStartup && !isDevelopment)
        {
            return ValidateOptionsResult.Fail(
                "Database:MigrateOnStartup must be false outside Development (DEPLOY-016). " +
                "Migrations are applied by the mtf-migrator container as a separate deployment step.");
        }

        return ValidateOptionsResult.Success;
    }
}
