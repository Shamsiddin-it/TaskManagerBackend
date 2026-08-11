using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Contracts.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// The caller's own organization (Приложение D.0).
/// </summary>
/// <remarks>
/// Singular and identifier-free by design. There is no <c>POST</c>: an organization is created only by
/// the provisioning command, and the endpoint is <b>absent from the contract</b> rather than merely
/// forbidden by policy (<c>ORG-001</c>, <c>ORG-002</c>). There is no <c>DELETE</c> either
/// (<c>ORG-006</c>).
/// </remarks>
[ApiController]
[Route("api/v1/organization")]
[Produces("application/json")]
public sealed class OrganizationController(IOrganizationService organizationService) : ControllerBase
{
    /// <summary>
    /// <c>GET /organization</c> — the minimal view for anyone, the full one for an Organization Admin.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = MtfPolicies.Authenticated)]
    [ProducesResponseType<OrganizationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetAsync(CancellationToken cancellationToken) =>
        Ok(await organizationService.GetAsync(cancellationToken));

    /// <summary><c>PUT /organization</c> — Organization Admin of their own organization only.</summary>
    [HttpPut]
    [Authorize(Policy = MtfPolicies.OrganizationAdmin)]
    [ProducesResponseType<OrganizationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OrganizationDto>> UpdateAsync(
        UpdateOrganizationRequest request,
        CancellationToken cancellationToken) =>
        Ok(await organizationService.UpdateAsync(request, cancellationToken));
}
