using System.Text.Json;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>
/// One logical notification, before the channel policy has been applied.
/// </summary>
/// <remarks>
/// There is no channel here and no deduplication key. Both are derived: the channel from the event's
/// policy and the recipient's Telegram binding (18.1), the key from the template of <c>NTF-015</c>.
/// Letting a call site supply either invites two mistakes the type system can otherwise prevent — a
/// policy that drifts per caller, and a key missing the tenant prefix or the channel.
/// </remarks>
public sealed record OutboxEntry
{
    public required Guid RecipientUserId { get; init; }

    public required string EventType { get; init; }

    /// <summary>The object the notification is about — the assignment, the submission, the branch.</summary>
    public required Guid EntityId { get; init; }

    /// <summary>
    /// Carries no tokens, presigned URLs, passwords, file contents or third-party personal data
    /// (<c>NTF-017</c>). The link in the message points at the application, where access is checked
    /// again.
    /// </summary>
    public required JsonDocument Payload { get; init; }

    public Guid? CategoryId { get; init; }

    /// <summary>
    /// Distinguishes repeats of one event on one object — the deadline value for a reminder, the event
    /// sequence number for a status change (<c>NTF-015</c>).
    /// </summary>
    public string? Discriminator { get; init; }

    /// <summary>
    /// Marks an alert about the notification system itself, which produces no further alert when it
    /// fails (<c>NTF-020</c>, <c>NTF-021</c>).
    /// </summary>
    public bool IsSystemAlert { get; init; }
}

/// <summary>
/// Enqueues notifications into the transactional outbox (TZ 18.4).
/// </summary>
/// <remarks>
/// Rows join the current unit of work and commit with the business event that produced them
/// (<c>NTF-009</c>): the notification cannot be lost if the process dies, and cannot be sent if the
/// transaction rolls back.
/// </remarks>
public interface IOutboxWriter
{
    /// <summary>Enqueues for the caller's effective scope.</summary>
    Task EnqueueAsync(OutboxEntry entry, CancellationToken cancellationToken);

    /// <summary>Enqueues with explicit scope, for background tasks and for provisioning.</summary>
    Task EnqueueSystemAsync(
        OutboxEntry entry,
        Guid organizationId,
        Guid? branchId,
        CancellationToken cancellationToken);
}
