using System.ComponentModel.DataAnnotations;

namespace MentorTaskFlow.Infrastructure.Options;

/// <summary>
/// Hangfire and the background jobs of TZ 20.
/// </summary>
/// <remarks>
/// <see cref="EnableServer"/> is off by default so only <c>mtf-worker</c> runs jobs. One Hangfire
/// instance serves the whole installation — there is no scheduler per branch (<c>TEN-054</c>) — and
/// several servers would still be safe thanks to <c>DisableConcurrentExecution</c>, but they would
/// duplicate nothing useful.
/// </remarks>
public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>Feature flag of 4.1: with it off no job is registered and none runs.</summary>
    public bool Enabled { get; init; }

    /// <summary>Runs the Hangfire server in this process.</summary>
    public bool EnableServer { get; init; }

    /// <summary>
    /// Hangfire's own tables live outside <c>public</c>.
    /// </summary>
    /// <remarks>
    /// ADR-002: the scheduler's schema is not part of the domain model and must not appear in EF
    /// migrations, where it would be diffed and eventually dropped by a scaffolded migration.
    /// </remarks>
    [Required]
    public string Schema { get; init; } = "hangfire";

    /// <summary>Overdue and reminder passes, in minutes (20.1).</summary>
    [Range(1, 1440)]
    public int OverdueIntervalMinutes { get; init; } = 15;

    /// <summary>Assignments per transaction in the overdue pass (<c>SCH-019</c>).</summary>
    [Range(1, 5000)]
    public int OverdueBatchSize { get; init; } = 200;

    /// <summary>Local hour at which suggestions are generated (<c>SCH-001</c>).</summary>
    [Range(0, 23)]
    public int AutoGenerationHour { get; init; } = 6;
}
