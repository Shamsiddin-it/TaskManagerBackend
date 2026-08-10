namespace MentorTaskFlow.Contracts.Admin;

/// <summary>One AuditLog row as returned by <c>GET /admin/audit-log</c> (Приложение D.7).</summary>
/// <remarks>
/// <c>Metadata</c> is omitted from the list projection: it is per-action and may be large, and the
/// list exists for navigation. Nothing here can carry a secret — <c>AUD-022</c> forbids storing one in
/// the first place.
/// </remarks>
public sealed record AuditLogEntryDto(
    Guid Id,
    string Action,
    string EntityType,
    Guid? EntityId,
    Guid? BranchId,
    Guid? CategoryId,
    string ActorType,
    Guid? ActorId,
    string? ActorRole,
    string? ActorAdminScope,
    string Result,
    string? FailureReason,
    string? HttpMethod,
    string? Path,
    DateTimeOffset OccurredAt,
    Guid CorrelationId);

/// <summary>Filters for <c>GET /admin/audit-log</c>.</summary>
/// <remarks>
/// There is no <c>organizationId</c> and no way to widen the scope: the organization always comes from
/// the principal, and a Branch Admin is pinned to their own branch regardless of what is sent
/// (<c>SEC-003</c>, <c>TEN-040</c>).
/// </remarks>
public sealed record AuditLogQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    /// <summary>Narrows to one action code.</summary>
    public string? Action { get; init; }

    public Guid? ActorId { get; init; }

    public string? EntityType { get; init; }

    public Guid? EntityId { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }
}
