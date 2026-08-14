using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Contracts.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// The Lead's decision on a submitted version (Приложение D.5, TZ 15.7).
/// </summary>
/// <remarks>
/// There is no <c>PUT</c> and no <c>DELETE</c>, for an administrator either. A verdict that could be
/// rewritten afterwards would make the history of a task useless in exactly the dispute it exists to
/// settle (<c>REV-020</c>).
/// </remarks>
[ApiController]
[Route("api/v1/submissions/{id:guid}")]
[Produces("application/json")]
public sealed class ReviewsController(IReviewService reviews) : ControllerBase
{
    /// <summary>
    /// <c>POST /submissions/{id}/reviews</c> — approve, or return the work with a new deadline.
    /// </summary>
    /// <remarks>
    /// Reserved for the Lead of the assignment's own category. No administrator takes part in the
    /// study cycle; version 2.0's «Lead/Admin» wording was removed in 2.1 (<c>ASN-025</c>).
    /// </remarks>
    [HttpPost("reviews")]
    [Authorize(Policy = MtfPolicies.Lead)]
    [ProducesResponseType<ReviewDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReviewDto>> CreateAsync(
        Guid id,
        CreateReviewRequest request,
        CancellationToken cancellationToken)
    {
        var review = await reviews.CreateAsync(id, request, cancellationToken);

        return CreatedAtRoute(RouteNames.GetReview, new { id }, review);
    }

    /// <summary><c>GET /submissions/{id}/review</c> — 404 while no decision has been made.</summary>
    [HttpGet("review", Name = RouteNames.GetReview)]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<ReviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReviewDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Ok(await reviews.GetAsync(id, cancellationToken));
}
