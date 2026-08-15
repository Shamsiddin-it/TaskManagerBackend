using MentorTaskFlow.Contracts.Analytics;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>Reports over the study cycle (TZ 21).</summary>
public interface IAnalyticsService
{
    /// <summary>
    /// One person's own metrics, broken down by category (<c>ANA-014</c>).
    /// </summary>
    /// <remarks>
    /// A Mentor sees their whole history within their <b>current</b> branch: ownership never crosses a
    /// branch boundary, so work done before a transfer is not in their personal report either
    /// (<c>USER-016</c>, <c>USER-017</c>).
    /// </remarks>
    Task<ReportDto> GetPersonalAsync(ReportQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Team metrics, grouped by branch and category.
    /// </summary>
    /// <remarks>
    /// For a Mentor the answer is anonymised and requires at least five distinct mentors <b>inside the
    /// requested scope</b> — combining branches to reach the threshold is refused rather than allowed
    /// (<c>ANA-012</c>, <c>TEN-072</c>).
    /// </remarks>
    Task<ReportDto> GetTeamAsync(ReportQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Branch comparison — Organization Admin only (<c>TEN-070</c>).
    /// </summary>
    /// <remarks>
    /// The one report that aggregates across branches, and it says so: <c>isCrossBranchAggregate</c>
    /// with the list of branches included (<c>TEN-071</c>).
    /// </remarks>
    Task<ReportDto> GetBranchComparisonAsync(ReportQuery query, CancellationToken cancellationToken);
}
