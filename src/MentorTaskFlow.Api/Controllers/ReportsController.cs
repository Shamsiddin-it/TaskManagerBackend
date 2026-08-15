using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Contracts.Analytics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// Reports over the study cycle (Приложение D.6, TZ 21).
/// </summary>
/// <remarks>
/// Every response carries <c>periodTimeZoneId</c>, <c>isPartialPeriod</c> and
/// <c>isCrossBranchAggregate</c>. Without them a set of figures cannot be interpreted: the reader
/// would not know which zone bounded the period, whether it has finished, or whether branches were
/// combined (<c>ANA-006</c>, <c>TEN-071</c>, <c>TEN-074</c>).
/// </remarks>
[ApiController]
[Route("api/v1/reports")]
[Produces("application/json")]
public sealed class ReportsController(IAnalyticsService analytics) : ControllerBase
{
    /// <summary>
    /// <c>GET /reports/personal</c> — one person's metrics, broken down by category.
    /// </summary>
    /// <remarks>
    /// A Mentor may name only themselves; anyone else is 400 rather than a quietly narrowed answer,
    /// because a sequence of narrow queries is how an anonymised aggregate gets unpicked
    /// (<c>ANA-013</c>).
    /// </remarks>
    [HttpGet("personal")]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<ReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ReportDto>> GetPersonalAsync(
        [FromQuery] ReportQuery query,
        CancellationToken cancellationToken) =>
        Ok(await analytics.GetPersonalAsync(query, cancellationToken));

    /// <summary>
    /// <c>GET /reports/team</c> — grouped by branch and category, never by category name.
    /// </summary>
    /// <remarks>
    /// For a Mentor the figures are anonymised and require at least five mentors inside the requested
    /// scope; below that the answer is 403 with no partial data of any kind (<c>ANA-012</c>,
    /// <c>TEN-072</c>).
    /// </remarks>
    [HttpGet("team")]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<ReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReportDto>> GetTeamAsync(
        [FromQuery] ReportQuery query,
        CancellationToken cancellationToken) =>
        Ok(await analytics.GetTeamAsync(query, cancellationToken));

    /// <summary>
    /// <c>GET /reports/branches</c> — Organization Admin only.
    /// </summary>
    /// <remarks>
    /// The one report that combines branches, and it says so in the response along with the list of
    /// branches included (<c>TEN-071</c>, <c>TEN-073</c>).
    /// </remarks>
    [HttpGet("branches")]
    [Authorize(Policy = MtfPolicies.OrganizationAdmin)]
    [ProducesResponseType<ReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ReportDto>> GetBranchComparisonAsync(
        [FromQuery] ReportQuery query,
        CancellationToken cancellationToken) =>
        Ok(await analytics.GetBranchComparisonAsync(query, cancellationToken));
}
