using MentorTaskFlow.Contracts.Tenancy;

namespace MentorTaskFlow.Contracts.Analytics;

/// <summary>
/// Filters for the report endpoints (<c>ANA-010</c>).
/// </summary>
/// <remarks>
/// <c>from</c> and <c>to</c> are <b>local dates of the reporting zone</b>, both inclusive; the server
/// turns them into a half-open UTC interval (<c>ANA-002</c>). Passing instants instead would make the
/// boundary depend on the caller's clock.
/// </remarks>
public sealed record ReportQuery
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    /// <summary>Organization Admin only; anyone else is fixed to their own (<c>TEN-070</c>).</summary>
    public Guid? BranchId { get; init; }

    public Guid? CategoryId { get; init; }

    /// <summary>A Mentor may name only themselves — anything else is 400 (<c>ANA-013</c>).</summary>
    public Guid? MentorId { get; init; }

    public string? AssignmentType { get; init; }

    public string? Source { get; init; }

    /// <summary>
    /// Draft and Suggested are never counted — they are not work. Cancelled joins only on request
    /// (<c>ANA-001</c>).
    /// </summary>
    public bool IncludeCancelled { get; init; }
}

/// <summary>
/// A duration metric: the median leads, the mean follows (<c>ANA-008</c>).
/// </summary>
/// <remarks>
/// The median is primary because one task forgotten for a fortnight moves the mean far enough to make
/// it uninformative — and reports are read to find out whether the usual case is healthy.
/// </remarks>
public sealed record DurationMetricDto(double? MedianHours, double? MeanHours, int SampleSize);

/// <summary>The ten metrics of 21.2.</summary>
/// <remarks>
/// Every ratio is nullable, and a null denominator yields null rather than zero (<c>ANA-004</c>):
/// «no data» and «zero percent» are different answers, and showing the second for the first misleads.
/// </remarks>
public sealed record MetricsDto(
    int TotalAssignments,
    int ApprovedAssignments,
    double? FirstPassApprovalRate,
    DurationMetricDto InitialSubmissionTime,
    DurationMetricDto FirstReviewResponseTime,
    DurationMetricDto FinalReviewTime,
    DurationMetricDto TotalCycleTime,
    double? AverageVersions,
    double? OverdueRate,
    double? LateSubmissionRate);

/// <summary>
/// One breakdown row.
/// </summary>
/// <remarks>
/// <c>TEN-073</c>: in an all-branches report every row names its branch. A row without one would let
/// two identically named categories of different branches be read as the same line.
/// </remarks>
public sealed record ReportRowDto(
    BranchSummaryDto? Branch,
    Guid? CategoryId,
    string? CategoryName,
    Guid? MentorId,
    string? MentorFullName,
    MetricsDto Metrics);

/// <summary>
/// A report and the header that makes it interpretable.
/// </summary>
/// <remarks>
/// <para>
/// <c>periodTimeZoneId</c> is mandatory (<c>TEN-074</c>): a branch report is bounded by the branch's
/// zone and an organization report by the head office's, and a reader cannot tell which without
/// being told.
/// </para>
/// <para>
/// <c>isPartialPeriod</c> marks a period that has not finished, so a comparison against it is known to
/// be approximate rather than quietly wrong (<c>ANA-006</c>).
/// </para>
/// </remarks>
public sealed record ReportDto(
    DateOnly From,
    DateOnly To,
    string PeriodTimeZoneId,
    bool IsPartialPeriod,
    bool IsCrossBranchAggregate,
    IReadOnlyList<Guid> IncludedBranchIds,
    MetricsDto Total,
    IReadOnlyList<ReportRowDto> Rows);
