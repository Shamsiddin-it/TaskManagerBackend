namespace MentorTaskFlow.Infrastructure.Analytics;

/// <summary>How a report's rows are broken down.</summary>
/// <remarks>
/// A closed set, and deliberately so: the grouping columns are interpolated into SQL, and anything a
/// caller could influence there would be an injection point. Nothing outside this enum ever reaches
/// the query.
/// </remarks>
public enum ReportGrouping
{
    /// <summary>A single total row.</summary>
    None = 0,

    /// <summary>By branch and category together — never by category name (<c>TEN-071</c>).</summary>
    BranchCategory = 1,

    /// <summary>By branch, for the organization-level comparison.</summary>
    Branch = 2,

    /// <summary>By mentor, for a Lead looking at their own team.</summary>
    Mentor = 3,
}

/// <summary>
/// The scope a report actually runs in, after the caller's role has narrowed it.
/// </summary>
/// <remarks>
/// Resolved once and passed down, so no query can be written against a scope the caller merely asked
/// for rather than one they hold (<c>TEN-070</c>).
/// </remarks>
public sealed record ReportScope
{
    public required Guid OrganizationId { get; init; }

    /// <summary>Null only for an Organization Admin comparing branches.</summary>
    public Guid? BranchId { get; init; }

    public Guid? CategoryId { get; init; }

    /// <summary>Set when the report is about one person.</summary>
    public Guid? MentorId { get; init; }

    public required ReportGrouping Grouping { get; init; }

    /// <summary>Whether rows from more than one branch may be combined (<c>TEN-071</c>).</summary>
    public bool IsCrossBranch { get; init; }
}
