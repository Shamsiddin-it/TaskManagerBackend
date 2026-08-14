namespace MentorTaskFlow.Contracts.Notifications;

/// <summary>
/// One outbox row as an administrator sees it (Приложение D.7).
/// </summary>
/// <remarks>
/// The payload is not exposed. It exists to render a message and may name a task or a person; the
/// journal is for operating the queue — what failed, how often, and why — not for reading other
/// people's notifications (<c>AUD-022</c>).
/// </remarks>
public sealed record NotificationDto(
    Guid Id,
    Guid UserId,
    Guid? BranchId,
    Guid? CategoryId,
    string EventType,
    string Channel,
    string Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? SentAt,
    string? LastError,
    bool IsSystemAlert,
    DateTimeOffset CreatedAt);

/// <summary>Filters for <c>GET /admin/notifications</c> (<c>TEN-046</c>).</summary>
public sealed record NotificationListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    public string? Status { get; init; }

    public string? Channel { get; init; }

    public string? EventType { get; init; }
}
