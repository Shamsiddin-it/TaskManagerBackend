using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// User administration (Приложение D.2, TZ 15.1, 39.5).
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>DELETE</c>. Physical deletion of a user is forbidden and every foreign key to them
/// is <c>ON DELETE RESTRICT</c>; deactivation is the supported path (<c>USER-022</c>).
/// </para>
/// <para>
/// <c>change-category</c> and <c>change-branch</c> are absent from this phase on purpose. Both are
/// blocked by unfinished assignments (<c>USER-012</c>, <c>BRN-038</c>), and assignments do not exist
/// yet — implementing the transfers without their blocking condition would ship an operation that
/// silently strands work.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/users")]
[Produces("application/json")]
public sealed class UsersController(IUserService userService) : ControllerBase
{
    /// <summary>
    /// <c>GET /users</c> — Organization Admin, Branch Admin and Lead, each within their own reach.
    /// </summary>
    /// <remarks>
    /// A Mentor is refused: they may not see the personal data or individual results of other
    /// mentors, not even within their own category (<c>USER-010</c>, TZ 8.4).
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<PagedResult<UserDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<UserDto>>> ListAsync(
        [FromQuery] UserListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await userService.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}", Name = RouteNames.GetUser)]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Ok(await userService.GetAsync(id, cancellationToken));

    /// <summary>
    /// <c>POST /users</c> — creates the account and sends the invitation.
    /// </summary>
    /// <remarks>
    /// No password is generated, mailed or shown. The account exists without one, occupies its email
    /// and counts towards the single-active-Lead rule, and the person sets their own password through
    /// a one-time link (<c>AUTH-019</c>, <c>AUTH-021</c>).
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userService.CreateAsync(request, cancellationToken);

        return CreatedAtRoute(RouteNames.GetUser, new { id = user.Id }, user);
    }

    /// <summary><c>PATCH /users/{id}</c> — the display name and nothing else (<c>API-009</c>).</summary>
    [HttpPatch("{id:guid}")]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserDto>> PatchAsync(
        Guid id,
        PatchUserRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.PatchAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> ActivateAsync(
        Guid id,
        UserActionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.ActivateAsync(id, request, cancellationToken));

    /// <summary>
    /// <c>POST /users/{id}/deactivate</c> — ends every session immediately.
    /// </summary>
    /// <remarks>
    /// Deactivating the last Lead of a category or the last administrator of a branch is permitted and
    /// raises a notification instead of being blocked: refusing would strand an organization whose
    /// sole administrator has left (<c>USER-005</c>, <c>USER-036</c>, <c>TEN-017</c>).
    /// </remarks>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserDto>> DeactivateAsync(
        Guid id,
        UserActionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.DeactivateAsync(id, request, cancellationToken));

    /// <summary>
    /// <c>POST /users/{id}/change-role</c>, which also carries a change of administrative contour.
    /// </summary>
    /// <remarks>
    /// Assigning any administrator is reserved for an Organization Admin — a Branch Admin promoting
    /// somebody to Admin would be manufacturing a peer outside the organization's control
    /// (<c>USER-031</c>, <c>USER-033</c>).
    /// </remarks>
    [HttpPost("{id:guid}/change-role")]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserDto>> ChangeRoleAsync(
        Guid id,
        ChangeRoleRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.ChangeRoleAsync(id, request, cancellationToken));

    /// <summary>
    /// <c>POST /users/{id}/resend-invitation</c> — 202, and the previous link stops working.
    /// </summary>
    /// <remarks>
    /// Available to a Lead for their own mentors, so an expired invitation does not need an
    /// administrator's attention (<c>USER-009</c>).
    /// </remarks>
    [HttpPost("{id:guid}/resend-invitation")]
    [Authorize(Policy = MtfPolicies.LeadOrAdmin)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResendInvitationAsync(Guid id, CancellationToken cancellationToken)
    {
        await userService.ResendInvitationAsync(id, cancellationToken);

        return Accepted();
    }
}
