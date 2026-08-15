using MentorTaskFlow.Contracts.Analytics;

namespace MentorTaskFlow.Infrastructure.Analytics;

/// <summary>
/// Turns raw aggregates into the ten metrics of 21.2.
/// </summary>
/// <remarks>
/// Rounding happens here and nowhere earlier (<c>ANA-005</c>): percentages to one decimal, durations
/// to one decimal of an hour, while every intermediate step stays exact. Rounding inside the
/// aggregation would accumulate error across groups.
/// </remarks>
public static class MetricsMapper
{
    public static MetricsDto ToDto(MetricsRow row) => new(
        row.TotalAssignments,
        row.ApprovedAssignments,

        // 21.3: the denominator is the approved assignments of the same period, and zero of them
        // yields null rather than nought percent (ANA-004).
        Percentage(row.FirstPassApproved, row.ApprovedAssignments),
        Duration(row.InitialSubmissionMedian, row.InitialSubmissionMean, row.InitialSubmissionSamples),
        Duration(row.FirstReviewMedian, row.FirstReviewMean, row.FirstReviewSamples),
        Duration(row.FinalReviewMedian, row.FinalReviewMean, row.FinalReviewSamples),
        Duration(row.CycleMedian, row.CycleMean, row.CycleSamples),
        Ratio(row.SubmissionCount, row.AssignmentsWithSubmissions),

        // 21.4: unique assignments, never MarkedOverdue events — a task can slip twice, and counting
        // events would produce a rate above 100%.
        Percentage(row.OverdueAssignments, row.TotalAssignments),
        Percentage(row.LateSubmissions, row.PeriodSubmissions));

    /// <summary>
    /// A percentage, or null when there is nothing to divide by.
    /// </summary>
    /// <remarks>
    /// <c>ANA-004</c>: «no data» and «zero percent» are different answers, and returning the second
    /// for the first tells a reader the team performed badly when in fact nothing happened.
    /// </remarks>
    private static double? Percentage(int numerator, int denominator) =>
        denominator == 0 ? null : Math.Round(100d * numerator / denominator, 1);

    private static double? Ratio(int numerator, int denominator) =>
        denominator == 0 ? null : Math.Round((double)numerator / denominator, 1);

    private static DurationMetricDto Duration(double? median, double? mean, int samples) => new(
        median is { } m ? Math.Round(m, 1) : null,
        mean is { } a ? Math.Round(a, 1) : null,
        samples);
}
