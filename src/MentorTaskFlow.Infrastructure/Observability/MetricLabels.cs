namespace MentorTaskFlow.Infrastructure.Observability;

/// <summary>
/// The only label names any exported metric may use (<c>OBS-010</c>, <c>OBS-011</c>).
/// </summary>
/// <remarks>
/// <para>
/// An allowlist rather than a blocklist. The forbidden values — <c>userId</c>, <c>assignmentId</c>,
/// email, <c>correlationId</c> — are unbounded in cardinality: every new user or task creates a
/// permanent time series, and a collector that has run for a term stops being able to answer
/// anything. The second harm is quieter: <c>/metrics</c> is unauthenticated to the internal network,
/// so a label carrying an email turns a monitoring endpoint into a directory.
/// </para>
/// <para>
/// Naming a blocklist would mean guessing what the next identifier is called. The set below is what
/// the metrics of 30.2 and 30.4 actually need, and anything outside it fails <c>TEST-SEC-023</c>.
/// </para>
/// </remarks>
public static class MetricLabels
{
    /// <summary><c>Organization.Slug</c> — stable, short, and not a UUID (<c>OBS-011</c>).</summary>
    public const string Organization = "organization";

    /// <summary><c>Branch.Code</c> — likewise stable and human-readable.</summary>
    public const string Branch = "branch";

    /// <summary>
    /// <c>Category.Id</c>.
    /// </summary>
    /// <remarks>
    /// The id and not the name, deliberately: <c>Category.Name</c> is editable and is not unique
    /// between branches, so two unrelated series would merge under one label the moment a second
    /// branch created a category called <c>C#</c> (<c>OBS-011</c>). Cardinality stays bounded by
    /// branches × categories, which is a number an operator controls.
    /// </remarks>
    public const string Category = "category";

    public const string Role = "role";

    public const string AdminScope = "admin_scope";

    public const string Reason = "reason";

    public const string Source = "source";

    public const string Channel = "channel";

    public const string Result = "result";

    public const string Direction = "direction";

    public const string Model = "model";

    public const string Method = "method";

    public const string Route = "route";

    public const string Status = "status";

    public const string Scope = "scope";

    public const string Job = "job";

    /// <summary>Every name above, for the architecture test that enforces the rule.</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        Organization, Branch, Category, Role, AdminScope, Reason, Source, Channel,
        Result, Direction, Model, Method, Route, Status, Scope, Job,
    };

    /// <summary>
    /// Names that must never appear, whatever else changes.
    /// </summary>
    /// <remarks>
    /// Redundant with <see cref="Allowed"/> and kept anyway: it states the intent in the words
    /// <c>OBS-010</c> uses, so a reader of a failing test sees the rule rather than a set difference.
    /// </remarks>
    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "userId", "user_id", "assignmentId", "assignment_id", "submissionId", "submission_id",
        "reviewId", "review_id", "email", "telegramChatId", "telegram_chat_id",
        "correlationId", "correlation_id", "path", "url", "ip", "ipAddress",
    };
}
