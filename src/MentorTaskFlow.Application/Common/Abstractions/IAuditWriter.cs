using System.Text.Json;
using MentorTaskFlow.Domain.Auditing;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>
/// One administrative or system action to record.
/// </summary>
/// <remarks>
/// Deliberately carries no <c>OrganizationId</c>, <c>BranchId</c>, actor or correlation id: the writer
/// takes those from <c>IBranchContext</c> and <c>ICurrentUserContext</c>, never from arguments
/// (<c>TEN-022</c>). A caller that could pass its own scope could also pass the wrong one, and the
/// audit trail would record actions against the wrong tenant.
/// </remarks>
public sealed record AuditEntry
{
    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public Guid? EntityId { get; init; }

    /// <summary>Narrows the record to one category when the action concerns a specific one.</summary>
    public Guid? CategoryId { get; init; }

    public AuditResult Result { get; init; } = AuditResult.Success;

    /// <summary>An error code. Never a message that could carry a secret (<c>AUD-022</c>).</summary>
    public string? FailureReason { get; init; }

    /// <summary>Redacted values only (TZ 27.4).</summary>
    public JsonDocument? Metadata { get; init; }
}

/// <summary>
/// Appends to the AuditLog (TZ 10.14).
/// </summary>
/// <remarks>
/// Entries are added to the current unit of work rather than saved immediately, so the record commits
/// in the same transaction as the action it describes. An audit row that survives a rolled-back
/// action would describe something that never happened.
/// </remarks>
public interface IAuditWriter
{
    /// <summary>Records an action performed by the authenticated caller.</summary>
    void Write(AuditEntry entry);

    /// <summary>
    /// Records an action of a background task (<c>ActorType='System'</c>).
    /// </summary>
    /// <remarks>
    /// Scope is explicit here because a system task has no request context to take it from. Every such
    /// task applies scope itself and is on the registered exception list of <c>SEC-031</c>.
    /// </remarks>
    void WriteSystem(AuditEntry entry, Guid organizationId, Guid? branchId, Guid? correlationId = null);
}
