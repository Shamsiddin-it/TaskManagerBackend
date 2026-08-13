using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Schedule;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// The category schedule (Приложение D.4, TZ 15.4).
/// </summary>
/// <remarks>
/// <para>
/// A Mentor reads and never writes. The plan is the Lead's instrument, and a mentor able to edit it
/// could hand themselves work other than the curriculum prescribes (<c>TOPIC-004</c>).
/// </para>
/// <para>
/// <b>Beyond Приложение D.4:</b> <c>activate</c> and <c>deactivate</c> are added. <c>TOPIC-012</c> and
/// <c>TPL-002</c> require deactivation as the alternative once deletion is refused, but the endpoint
/// catalogue lists no operation that reaches <c>IsActive</c>. The capability is mandated and the route
/// is not, so it follows the action-endpoint convention already used for branches and categories
/// (<c>API-008</c>).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/topics")]
[Produces("application/json")]
public sealed class TopicsController(IScheduleService scheduleService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<PagedResult<TopicDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<TopicDto>>> ListAsync(
        [FromQuery] TopicListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await scheduleService.ListTopicsAsync(query, cancellationToken));

    [HttpGet("{id:guid}", Name = RouteNames.GetTopic)]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<TopicDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TopicDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Ok(await scheduleService.GetTopicAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<TopicDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TopicDto>> CreateAsync(
        CreateTopicRequest request,
        CancellationToken cancellationToken)
    {
        var topic = await scheduleService.CreateTopicAsync(request, cancellationToken);

        return CreatedAtRoute(RouteNames.GetTopic, new { id = topic.Id }, topic);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<TopicDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TopicDto>> UpdateAsync(
        Guid id,
        UpdateTopicRequest request,
        CancellationToken cancellationToken) =>
        Ok(await scheduleService.UpdateTopicAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<TopicDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TopicDto>> ActivateAsync(
        Guid id,
        ScheduleActionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await scheduleService.ActivateTopicAsync(id, request, cancellationToken));

    /// <summary>
    /// <c>POST /topics/{id}/deactivate</c> — archives the topic (<c>TOPIC-013</c>).
    /// </summary>
    /// <remarks>
    /// It stops feeding auto-generation and stops appearing when creating assignments, while staying
    /// visible in the schedule. Assignments already created from it are untouched.
    /// </remarks>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<TopicDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TopicDto>> DeactivateAsync(
        Guid id,
        ScheduleActionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await scheduleService.DeactivateTopicAsync(id, request, cancellationToken));

    /// <summary>
    /// <c>DELETE /topics/{id}</c> — permitted only while nothing references the topic.
    /// </summary>
    /// <remarks>
    /// A topic with templates or assignments answers 409 <c>RESOURCE_IN_USE</c> and must be
    /// deactivated instead: deleting it would orphan work already handed out (<c>TOPIC-003</c>).
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await scheduleService.DeleteTopicAsync(id, cancellationToken);

        return NoContent();
    }

    /// <summary><c>GET /topics/{topicId}/assignments</c> — the templates of one topic.</summary>
    [HttpGet("{topicId:guid}/assignments")]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<IReadOnlyList<TopicAssignmentDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TopicAssignmentDto>>> ListAssignmentsAsync(
        Guid topicId,
        CancellationToken cancellationToken) =>
        Ok(await scheduleService.ListTopicAssignmentsAsync(topicId, cancellationToken));

    /// <summary>
    /// <c>POST /topics/{topicId}/assignments</c>.
    /// </summary>
    /// <remarks>
    /// The scope is inherited from the topic in the route, not supplied by the body — which is what
    /// makes <c>TPL-001</c> enforceable rather than merely stated.
    /// </remarks>
    [HttpPost("{topicId:guid}/assignments")]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<TopicAssignmentDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<TopicAssignmentDto>> CreateAssignmentAsync(
        Guid topicId,
        CreateTopicAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        var template = await scheduleService.CreateTopicAssignmentAsync(topicId, request, cancellationToken);

        return CreatedAtRoute(RouteNames.GetTopicAssignment, new { id = template.Id }, template);
    }
}

/// <summary>Work templates addressed directly (Приложение D.4).</summary>
[ApiController]
[Route("api/v1/topic-assignments")]
[Produces("application/json")]
public sealed class TopicAssignmentsController(IScheduleService scheduleService) : ControllerBase
{
    [HttpGet("{id:guid}", Name = RouteNames.GetTopicAssignment)]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<TopicAssignmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TopicAssignmentDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Ok(await scheduleService.GetTopicAssignmentAsync(id, cancellationToken));

    [HttpPut("{id:guid}")]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<TopicAssignmentDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TopicAssignmentDto>> UpdateAsync(
        Guid id,
        UpdateTopicAssignmentRequest request,
        CancellationToken cancellationToken) =>
        Ok(await scheduleService.UpdateTopicAssignmentAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<TopicAssignmentDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TopicAssignmentDto>> ActivateAsync(
        Guid id,
        ScheduleActionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await scheduleService.ActivateTopicAssignmentAsync(id, request, cancellationToken));

    /// <summary>
    /// <c>POST /topic-assignments/{id}/deactivate</c>.
    /// </summary>
    /// <remarks>
    /// Archiving instead of deletion: the template stops feeding auto-generation and stops appearing
    /// for new assignments, and everything already created from it is untouched (<c>TPL-003</c>).
    /// </remarks>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<TopicAssignmentDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TopicAssignmentDto>> DeactivateAsync(
        Guid id,
        ScheduleActionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await scheduleService.DeactivateTopicAssignmentAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await scheduleService.DeleteTopicAssignmentAsync(id, cancellationToken);

        return NoContent();
    }
}
