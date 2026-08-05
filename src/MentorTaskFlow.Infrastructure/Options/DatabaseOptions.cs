using System.ComponentModel.DataAnnotations;

namespace MentorTaskFlow.Infrastructure.Options;

/// <summary>
/// Database configuration. Validated at startup — a missing or malformed value aborts the boot with a
/// readable message rather than falling back to an unsafe default (<c>DEPLOY-015</c>).
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Runs <c>Database.Migrate()</c> during API startup. Allowed only in Development.
    /// In Test, Staging and Production migrations are applied exclusively by the <c>mtf-migrator</c>
    /// container, because several API replicas starting at once would race for schema locks
    /// (<c>DEPLOY-016</c>). Enforced by <see cref="DatabaseOptionsValidator"/>.
    /// </summary>
    public bool MigrateOnStartup { get; init; }

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    [Range(0, 10)]
    public int MaxRetryCount { get; init; } = 3;
}
