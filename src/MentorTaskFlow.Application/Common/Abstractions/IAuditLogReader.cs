using MentorTaskFlow.Contracts.Admin;
using MentorTaskFlow.Contracts.Common;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>
/// Reads the AuditLog within the caller's administrative contour (<c>AUD-002</c>, <c>TEN-040</c>).
/// </summary>
/// <remarks>
/// The act of reading is itself audited (<c>audit.read</c>) with the scope that was applied, so an
/// investigation can establish how much was viewed and by whom (<c>AUD-023</c>).
/// </remarks>
public interface IAuditLogReader
{
    Task<PagedResult<AuditLogEntryDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken);
}
