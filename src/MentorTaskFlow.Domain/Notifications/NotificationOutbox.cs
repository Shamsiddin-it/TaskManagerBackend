using System.Text.Json;
using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Notifications;

public enum NotificationChannel
{
    Email = 0,
    Telegram = 1,
}

/// <summary>
/// Delivery states (TZ 18.4).
/// </summary>
/// <remarks>
/// Version 2.0's <c>Failed</c> was removed in 2.1 as a duplicate of <see cref="DeadLetter"/>: two
/// states meaning «did not arrive» led to retry logic that had to handle both and inevitably handled
/// them differently.
/// </remarks>
public enum NotificationStatus
{
    Pending = 0,
    Processing = 1,
    Sent = 2,
    DeadLetter = 3,
}

/// <summary>
/// A transactional outbox row for one outgoing notification (TZ 10.15).
/// </summary>
/// <remarks>
/// <para>
/// Written in the <b>same transaction</b> as the business event it describes (<c>NTF-009</c>). That is
/// the whole mechanism: the notification cannot be lost if the process dies, and cannot be sent if the
/// transaction rolls back.
/// </para>
/// <para>
/// Scope is copied from the aggregate that raised the event and is immutable — never recomputed at
/// delivery time. Moving the recipient to another branch between creation and sending must not change
/// where the notification was addressed (<c>TEN-041</c>).
/// </para>
/// </remarks>
public sealed class NotificationOutbox : BaseEntity
{
    public const int MaxAttempts = 5;
    public const int EventTypeMaxLength = 48;
    public const int DeduplicationKeyMaxLength = 200;
    public const int LastErrorMaxLength = 1000;

    public Guid UserId { get; private set; }

    /// <summary>Always required; fixed at creation and immutable.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// Null only for the three organization-level event types of <c>TEN-042</c>, enforced by
    /// <c>ck_notification_outbox_branch_scope</c>.
    /// </summary>
    public Guid? BranchId { get; private set; }

    public Guid? CategoryId { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public string EventType { get; private set; } = null!;

    /// <summary>Carries no tokens, passwords, presigned URLs or third-party personal data (<c>NTF-017</c>).</summary>
    public JsonDocument Payload { get; private set; } = null!;

    public int PayloadSchemaVersion { get; private set; }

    public NotificationStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// Unique. Includes the tenant scope, without which same-named events from two branches would
    /// collide on the unique index and one would <b>silently suppress</b> the other — a leak that never
    /// surfaces as an error (<c>NTF-015</c>, <c>TEN-043</c>).
    /// </summary>
    public string DeduplicationKey { get; private set; } = null!;

    public string? ProviderMessageId { get; private set; }

    /// <summary>
    /// Marks an alert about the notification system itself. Such a row, on failing, produces no
    /// further notification: otherwise an unreachable mail provider would generate an alert about the
    /// failed alert, forever (<c>NTF-020</c>, <c>NTF-021</c>).
    /// </summary>
    public bool IsSystemAlert { get; private set; }

    public DateTimeOffset? LockedAt { get; private set; }

    public string? LockedBy { get; private set; }

    private NotificationOutbox()
    {
    }

    public static NotificationOutbox Enqueue(
        Guid userId,
        Guid organizationId,
        Guid? branchId,
        Guid? categoryId,
        NotificationChannel channel,
        string eventType,
        JsonDocument payload,
        string deduplicationKey,
        DateTimeOffset now,
        bool isSystemAlert = false)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "NotificationOutbox.OrganizationId обязателен всегда (TEN-041).");
        }

        if (string.IsNullOrWhiteSpace(eventType) || eventType.Length > EventTypeMaxLength)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "EventType обязателен.");
        }

        if (string.IsNullOrWhiteSpace(deduplicationKey) || deduplicationKey.Length > DeduplicationKeyMaxLength)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "DeduplicationKey обязателен.");
        }

        if (branchId is null && !NotificationEventTypes.OrganizationLevelEvents.Contains(eventType))
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"BranchId обязателен для события '{eventType}' (TEN-042).");
        }

        return new NotificationOutbox
        {
            UserId = userId,
            OrganizationId = organizationId,
            BranchId = branchId,
            CategoryId = categoryId,
            Channel = channel,
            EventType = eventType,
            Payload = payload,
            PayloadSchemaVersion = 1,
            Status = NotificationStatus.Pending,
            Attempts = 0,
            NextAttemptAt = now,
            DeduplicationKey = deduplicationKey,
            IsSystemAlert = isSystemAlert,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// The backoff of <c>NTF-013</c>: 1 min → 5 min → 15 min → 1 h → 6 h.
    /// </summary>
    /// <remarks>
    /// Indexed by the attempt that just failed, so the first retry waits a minute and the fifth failure
    /// has no delay to compute — it goes to the dead letter instead.
    /// </remarks>
    public static readonly IReadOnlyList<TimeSpan> RetryBackoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];

    /// <summary>How long a captured row may stay in <see cref="NotificationStatus.Processing"/> (<c>NTF-012</c>).</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>Claims the row for one worker.</summary>
    /// <remarks>
    /// The batch is claimed in SQL with <c>FOR UPDATE SKIP LOCKED</c> so two workers never take the
    /// same row (<c>NTF-011</c>); this method records the capture on the entity that SQL returned.
    /// </remarks>
    public void Capture(string workerId, DateTimeOffset now)
    {
        if (Status is not NotificationStatus.Pending)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Захватить можно только запись в статусе Pending, а не {Status}.");
        }

        Status = NotificationStatus.Processing;
        LockedAt = now;
        LockedBy = workerId;
        LastAttemptAt = now;
        Attempts++;
    }

    public void MarkSent(string? providerMessageId, DateTimeOffset now)
    {
        EnsureProcessing();

        Status = NotificationStatus.Sent;
        SentAt = now;
        ProviderMessageId = providerMessageId;
        LastError = null;
        LockedAt = null;
        LockedBy = null;
    }

    /// <summary>
    /// A temporary failure: back to <see cref="NotificationStatus.Pending"/> with the next attempt
    /// scheduled, unless the attempts are exhausted.
    /// </summary>
    /// <returns><see langword="true"/> when the row was rescheduled, <see langword="false"/> when it dead-lettered.</returns>
    public bool RescheduleOrDeadLetter(string error, DateTimeOffset now)
    {
        EnsureProcessing();

        if (Attempts >= MaxAttempts)
        {
            SendToDeadLetter(error, now);
            return false;
        }

        Status = NotificationStatus.Pending;
        NextAttemptAt = now.Add(RetryBackoff[Math.Min(Attempts - 1, RetryBackoff.Count - 1)]);
        LastError = Truncate(error);
        LockedAt = null;
        LockedBy = null;

        return true;
    }

    /// <summary>
    /// A permanent failure: no retry at all (<c>NTF-013</c>).
    /// </summary>
    /// <remarks>
    /// An invalid address or a blocked bot will not become valid by waiting, and five retries against
    /// a mailbox that does not exist is five more chances to be treated as a spammer.
    /// </remarks>
    public void SendToDeadLetter(string error, DateTimeOffset now)
    {
        EnsureProcessing();

        Status = NotificationStatus.DeadLetter;
        LastError = Truncate(error);
        LockedAt = null;
        LockedBy = null;
    }

    /// <summary>
    /// Returns a row whose lease has expired to the queue (<c>NTF-012</c>).
    /// </summary>
    /// <remarks>
    /// A process killed mid-send leaves its rows in <c>Processing</c> forever otherwise. The attempt is
    /// <b>not</b> given back: the send may well have reached the provider before the process died, and
    /// counting it protects against an unbounded loop of half-deliveries.
    /// </remarks>
    public void ReleaseExpiredLease(DateTimeOffset now)
    {
        if (Status is not NotificationStatus.Processing)
        {
            return;
        }

        Status = Attempts >= MaxAttempts ? NotificationStatus.DeadLetter : NotificationStatus.Pending;
        LastError = Truncate("Обработка прервана: истёк срок блокировки worker'а.");
        NextAttemptAt = now;
        LockedAt = null;
        LockedBy = null;
    }

    /// <summary>
    /// Admin's manual retry (<c>NTF-014</c>): only from the dead letter, and the counter starts over.
    /// </summary>
    /// <remarks>
    /// Resetting <see cref="Attempts"/> is deliberate. A manual retry follows a human decision that the
    /// cause has been dealt with — a mailbox fixed, a provider restored — so the row deserves the full
    /// budget rather than the one attempt its history left it.
    /// </remarks>
    public void RequeueByAdmin(DateTimeOffset now)
    {
        if (Status is not NotificationStatus.DeadLetter)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "Повторная отправка доступна только для записей в DeadLetter.");
        }

        Status = NotificationStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now;
        LastError = null;
        LockedAt = null;
        LockedBy = null;
    }

    private void EnsureProcessing()
    {
        if (Status is not NotificationStatus.Processing)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Операция допустима только для записи в статусе Processing, а не {Status}.");
        }
    }

    private static string Truncate(string error) =>
        error.Length <= LastErrorMaxLength ? error : error[..LastErrorMaxLength];
}
