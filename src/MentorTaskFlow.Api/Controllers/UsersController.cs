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
/// The two transfers are separate operations with separate authorisation, not one endpoint with an
/// optional branch: a category change stays inside one administrative contour, while a branch change
/// has one side outside any single Branch Admin's reach (<c>USER-037</c>, <c>BRN-036</c>).
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
    /// <c>POST /users/{id}/change-category</c> — a move within the user's own branch (TZ 15.2).
    /// </summary>
    /// <remarks>
    /// Refused while the person holds unfinished work or is the active Lead of their category: the
    /// answer carries the identifiers of the blocking tasks so the administrator can go and close them
    /// (<c>USER-012</c>, <c>USER-013</c>).
    /// </remarks>
    [HttpPost("{id:guid}/change-category")]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> ChangeCategoryAsync(
        Guid id,
        ChangeCategoryRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.ChangeCategoryAsync(id, request, cancellationToken));

    /// <summary>
    /// <c>POST /users/{id}/change-branch</c> — Organization Admin only (TZ 39.6).
    /// </summary>
    /// <remarks>
    /// A Branch Admin is refused for their own branch as much as for anyone else's. The transfer
    /// touches two branches at once, and an operation with one side outside the actor's contour cannot
    /// be authorised by them (<c>BRN-036</c>, <c>USER-030</c>). Historical work stays where it was
    /// created and the transferred user loses sight of it — ownership never crosses a branch boundary
    /// (<c>BRN-049</c>, <c>USER-017</c>).
    /// </remarks>
    [HttpPost("{id:guid}/change-branch")]
    [Authorize(Policy = MtfPolicies.OrganizationAdmin)]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> ChangeBranchAsync(
        Guid id,
        ChangeBranchRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.ChangeBranchAsync(id, request, cancellationToken));

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
