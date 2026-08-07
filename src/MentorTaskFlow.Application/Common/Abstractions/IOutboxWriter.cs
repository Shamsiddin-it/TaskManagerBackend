using System.Text.Json;
using MentorTaskFlow.Domain.Notifications;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>One notification to enqueue.</summary>
public sealed record OutboxEntry
{
    public required Guid RecipientUserId { get; init; }

    public required string EventType { get; init; }

    public required NotificationChannel Channel { get; init; }

    /// <summary>
    /// Without the mandatory <c>{organizationId}:{branchId|"org"}:</c> prefix, which the writer adds
    /// (<c>NTF-015</c>).
    /// </summary>
    public required string DeduplicationKey { get; init; }

    public required JsonDocument Payload { get; init; }

    public Guid? CategoryId { get; init; }

    public bool IsSystemAlert { get; init; }
}

/// <summary>
/// Enqueues notifications into the transactional outbox (TZ 18.4).
/// </summary>
/// <remarks>
/// Rows are added to the current unit of work, so they commit with the business event that produced
/// them (<c>NTF-009</c>). Delivery — the worker, <c>SKIP LOCKED</c>, retry, lease and dead-letter
/// handling — belongs to the notifications module; only the write side exists at this point, because
/// <c>ASN-023</c> requires the enqueue to be transactional with events that already have to be
/// recorded.
/// </remarks>
public interface IOutboxWriter
{
    /// <summary>Enqueues for the caller's effective scope.</summary>
    void Enqueue(OutboxEntry entry);

    /// <summary>Enqueues with explicit scope, for background tasks and for provisioning.</summary>
    void EnqueueSystem(OutboxEntry entry, Guid organizationId, Guid? branchId);
}
