using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>Route names used to build <c>Location</c> headers (<c>API-007</c>, TZ 23.2).</summary>
internal static class RouteNames
{
    public const string GetBranch = "branches.get";
}

/// <summary>
/// Branch lifecycle (Приложение D.0, TZ 39.1–39.3).
/// </summary>
/// <remarks>
/// <para>
/// Everything that changes a branch is Organization Admin only. A Branch Admin does not edit even
/// "harmless" fields such as the address: <c>TimeZoneId</c> decides the default for new categories
/// (<c>BRN-011</c>) and <c>Code</c> appears in organization-level reports and exports, and splitting
/// the fields into editable and not would create two validation rule sets and two test sets for one
/// secondary capability (<c>BRN-002</c>).
/// </para>
/// <para>
/// There is no <c>DELETE</c>. A branch is deactivated, never removed, and every FK to it is
/// <c>ON DELETE RESTRICT</c> (<c>BRN-022</c>).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/branches")]
[Produces("application/json")]
public sealed class BranchesController(IBranchService branchService) : ControllerBase
{
    /// <summary>
    /// <c>GET /branches</c> — Organization Admin only.
    /// </summary>
    /// <remarks>
    /// 403 for everyone else rather than an empty or filtered list: the roster of branches reveals the
    /// composition of the organization, which is outside branch isolation (<c>BRN-006</c>).
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = MtfPolicies.OrganizationAdmin)]
    [ProducesResponseType<PagedResult<BranchDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<BranchDto>>> ListAsync(
        [FromQuery] BranchListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await branchService.ListAsync(query, cancellationToken));

    /// <summary>
    /// <c>GET /branches/{id}</c> — full profile for an administrator, minimal for Lead and Mentor.
    /// </summary>
    /// <remarks>
    /// Open to any authenticated user because the object-level decision cannot be expressed by a
    /// policy: which branch a caller may read depends on the identifier, and a foreign one answers
    /// 404 in the service (<c>BRN-007</c>…<c>BRN-009</c>).
    /// </remarks>
    // Named explicitly: MVC strips the "Async" suffix from action names by default, so
    // CreatedAtAction(nameof(GetAsync)) resolves nothing and the 201 fails at formatting time. A
    // route name is immune to that convention.
    [HttpGet("{id:guid}", Name = RouteNames.GetBranch)]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<BranchDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Ok(await branchService.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = MtfPolicies.OrganizationAdmin)]
    [ProducesResponseType<BranchDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<BranchDto>> CreateAsync(
        CreateBranchRequest request,
        CancellationToken cancellationToken)
    {
        var branch = await branchService.CreateAsync(request, cancellationToken);

        return CreatedAtRoute(RouteNames.GetBranch, new { id = branch.Id }, branch);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = MtfPolicies.OrganizationAdmin)]
    [ProducesResponseType<BranchDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BranchDto>> UpdateAsync(
        Guid id,
        UpdateBranchRequest request,
        CancellationToken cancellationToken) =>
        Ok(await branchService.UpdateAsync(id, request, cancellationToken));

    /// <summary>
    /// <c>POST /branches/{id}/activate</c>.
    /// </summary>
    /// <remarks>
    /// Returns 200 with the whole updated object rather than 204: after a state change the client
    /// always needs the new status and the new <c>concurrencyToken</c>, and 204 would force a second
    /// request (<c>API-008</c>).
    /// </remarks>
    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = MtfPolicies.OrganizationAdmin)]
    [ProducesResponseType<BranchDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BranchDto>> ActivateAsync(
        Guid id,
        BranchActionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await branchService.ActivateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = MtfPolicies.OrganizationAdmin)]
    [ProducesResponseType<BranchDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BranchDto>> DeactivateAsync(
        Guid id,
        DeactivateBranchRequest request,
        CancellationToken cancellationToken) =>
        Ok(await branchService.DeactivateAsync(id, request, cancellationToken));

    /// <summary><c>POST /branches/{id}/make-head-office</c> (TZ 39.3).</summary>
    [HttpPost("{id:guid}/make-head-office")]
    [Authorize(Policy = MtfPolicies.OrganizationAdmin)]
    [ProducesResponseType<BranchDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BranchDto>> MakeHeadOfficeAsync(
        Guid id,
        BranchActionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await branchService.MakeHeadOfficeAsync(id, request, cancellationToken));
}
