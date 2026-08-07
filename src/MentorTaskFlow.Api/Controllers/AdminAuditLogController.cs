using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Contracts.Admin;
using MentorTaskFlow.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// <c>GET /admin/audit-log</c> (Приложение D.7).
/// </summary>
/// <remarks>
/// Restricted to the two administrative contours; Lead and Mentor have no access at all
/// (<c>AUD-002</c>). Which rows an administrator sees is decided in the reader, because the policy
/// cannot express «only your own branch».
/// </remarks>
[ApiController]
[Route("api/v1/admin/audit-log")]
[Produces("application/json")]
[Authorize(Policy = MtfPolicies.AnyAdmin)]
public sealed class AdminAuditLogController(
    IAuditLogReader auditLogReader,
    IUnitOfWork unitOfWork) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PagedResult<AuditLogEntryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AuditLogEntryDto>>> GetAsync(
        [FromQuery] AuditLogQuery query,
        CancellationToken cancellationToken)
    {
        var result = await auditLogReader.QueryAsync(query, cancellationToken);

        // The reader appends the audit.read row to the change tracker; committing it here keeps the
        // record of the read in the same unit of work as the read itself (AUD-023).
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Ok(result);
    }
}
