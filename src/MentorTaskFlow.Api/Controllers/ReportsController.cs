using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Analytics;
using MentorTaskFlow.Infrastructure.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
public sealed class ReportsController(
    IAnalyticsService analytics,
    IAiSummaryService aiSummaries,
    IOptions<AiOptions> aiOptions) : ControllerBase
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

    /// <summary>
    /// <c>POST /reports/ai-summary</c> — prose over the figures above (TZ 22).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 200 when the report already existed and 201 when this call generated it (<c>AI-013</c>). The
    /// distinction is not cosmetic: a 201 means the organization was billed for tokens, and a client
    /// that cannot tell the two apart cannot show a regeneration cost either.
    /// </para>
    /// <para>
    /// Synchronous, because the whole request is capped at ninety seconds (<c>AI-003</c>) and a
    /// background queue for a wait that short would add a polling protocol and gain nothing.
    /// </para>
    /// <para>
    /// With the feature off the endpoint answers 404 rather than 403, exactly as the Telegram bind
    /// endpoints do: a capability the installation does not have should be indistinguishable from one
    /// that does not exist. The metric endpoints above are untouched either way (<c>TEST-AI-002</c>).
    /// </para>
    /// </remarks>
    [HttpPost("ai-summary")]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<AiSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<AiSummaryDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AiSummaryDto>> GenerateAiSummaryAsync(
        [FromBody] AiSummaryRequest request,
        CancellationToken cancellationToken)
    {
        if (!aiOptions.Value.Enabled)
        {
            throw new NotFoundException();
        }

        var result = await aiSummaries.GenerateAsync(request, cancellationToken);

        return result.WasCreated
            ? StatusCode(StatusCodes.Status201Created, result.Summary)
            : Ok(result.Summary);
    }
}
