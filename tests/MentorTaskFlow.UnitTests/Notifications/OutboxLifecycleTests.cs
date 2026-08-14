using System.Text.Json;
using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Notifications;

namespace MentorTaskFlow.UnitTests.Notifications;

/// <summary>Delivery states, retry and the dead letter (TZ 18.4).</summary>
public sealed class OutboxLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_row_is_pending_and_due_immediately()
    {
        var row = Enqueue();

        row.Status.ShouldBe(NotificationStatus.Pending);
        row.Attempts.ShouldBe(0);
        row.NextAttemptAt.ShouldBe(Now);
        row.SentAt.ShouldBeNull();
    }

    [Fact]
    public void Capturing_marks_the_row_and_counts_the_attempt()
    {
        var row = Enqueue();

        row.Capture("worker-1", Now);

        row.Status.ShouldBe(NotificationStatus.Processing);
        row.Attempts.ShouldBe(1);
        row.LockedBy.ShouldBe("worker-1");
        row.LockedAt.ShouldBe(Now);
    }

    [Fact]
    public void A_captured_row_cannot_be_captured_again() =>
        Should.Throw<DomainException>(() =>
        {
            var row = Enqueue();
            row.Capture("worker-1", Now);
            row.Capture("worker-2", Now);
        });

    [Fact]
    public void A_delivered_row_records_the_provider_identifier()
    {
        var row = Captured();

        row.MarkSent("smtp-123", Now);

        row.Status.ShouldBe(NotificationStatus.Sent);
        row.SentAt.ShouldBe(Now);
        row.ProviderMessageId.ShouldBe("smtp-123");
        row.LockedBy.ShouldBeNull();
    }

    /// <summary><c>NTF-013</c>: 1 min → 5 min → 15 min → 1 h → 6 h.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    public void A_temporary_failure_is_rescheduled_by_the_backoff(int attempt, int expectedMinutes)
    {
        var row = Enqueue();

        for (var i = 0; i < attempt; i++)
        {
            row.Capture("worker-1", Now);
            row.RescheduleOrDeadLetter("таймаут SMTP", Now).ShouldBeTrue();
        }

        row.Status.ShouldBe(NotificationStatus.Pending);
        row.NextAttemptAt.ShouldBe(Now.AddMinutes(expectedMinutes));
        row.LastError.ShouldBe("таймаут SMTP");
    }

    [Fact]
    public void The_fifth_failure_ends_in_the_dead_letter()
    {
        var row = Enqueue();

        for (var i = 0; i < NotificationOutbox.MaxAttempts - 1; i++)
        {
            row.Capture("worker-1", Now);
            row.RescheduleOrDeadLetter("таймаут SMTP", Now).ShouldBeTrue();
        }

        row.Capture("worker-1", Now);

        row.RescheduleOrDeadLetter("таймаут SMTP", Now).ShouldBeFalse();
        row.Status.ShouldBe(NotificationStatus.DeadLetter);
        row.Attempts.ShouldBe(NotificationOutbox.MaxAttempts);
    }

    /// <summary>
    /// A permanent failure skips the retries entirely (<c>NTF-013</c>).
    /// </summary>
    /// <remarks>
    /// Five attempts at a mailbox that does not exist are not merely wasted: they are five more
    /// chances for the sending domain to be classified as a spam source.
    /// </remarks>
    [Fact]
    public void A_permanent_failure_skips_the_retries()
    {
        var row = Captured();

        row.SendToDeadLetter("SMTP 5.1.1 unknown mailbox", Now);

        row.Status.ShouldBe(NotificationStatus.DeadLetter);
        row.Attempts.ShouldBe(1);
    }

    /// <summary>
    /// <c>NTF-012</c>: a process killed mid-send leaves rows in <c>Processing</c>, which nothing else
    /// recovers — the claim query only ever looks at <c>Pending</c>.
    /// </summary>
    [Fact]
    public void An_expired_lease_returns_the_row_to_the_queue()
    {
        var row = Captured();

        row.ReleaseExpiredLease(Now.AddMinutes(6));

        row.Status.ShouldBe(NotificationStatus.Pending);
        row.LockedBy.ShouldBeNull();

        // The attempt is not given back: the send may have reached the provider before the process
        // died, and counting it bounds the loop of half-deliveries.
        row.Attempts.ShouldBe(1);
    }

    [Fact]
    public void An_expired_lease_on_the_last_attempt_dead_letters()
    {
        var row = Enqueue();

        for (var i = 0; i < NotificationOutbox.MaxAttempts; i++)
        {
            row.Capture("worker-1", Now);

            if (i < NotificationOutbox.MaxAttempts - 1)
            {
                row.RescheduleOrDeadLetter("таймаут", Now);
            }
        }

        row.ReleaseExpiredLease(Now.AddMinutes(6));

        row.Status.ShouldBe(NotificationStatus.DeadLetter);
    }

    /// <summary><c>NTF-014</c>: the manual retry follows a human decision, so the budget starts over.</summary>
    [Fact]
    public void An_admin_retry_resets_the_attempts()
    {
        var row = Captured();
        row.SendToDeadLetter("SMTP 5.1.1", Now);

        row.RequeueByAdmin(Now.AddHours(1));

        row.Status.ShouldBe(NotificationStatus.Pending);
        row.Attempts.ShouldBe(0);
        row.LastError.ShouldBeNull();
        row.NextAttemptAt.ShouldBe(Now.AddHours(1));
    }

    [Theory]
    [InlineData(NotificationStatus.Pending)]
    [InlineData(NotificationStatus.Sent)]
    public void An_admin_retry_is_refused_outside_the_dead_letter(NotificationStatus status)
    {
        var row = Enqueue();

        if (status is NotificationStatus.Sent)
        {
            row.Capture("worker-1", Now);
            row.MarkSent(null, Now);
        }

        Should.Throw<DomainException>(() => row.RequeueByAdmin(Now));
    }

    /// <summary>
    /// <c>TEN-042</c>: only three event types may carry a null branch, and the entity refuses the rest
    /// rather than leaving it to the CHECK constraint alone.
    /// </summary>
    [Fact]
    public void A_branch_scoped_event_requires_a_branch() =>
        Should.Throw<DomainException>(() => NotificationOutbox.Enqueue(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            branchId: null,
            categoryId: null,
            NotificationChannel.Email,
            NotificationEventTypes.AssignmentAssigned,
            JsonSerializer.SerializeToDocument(new { }),
            "key",
            Now));

    private static NotificationOutbox Captured()
    {
        var row = Enqueue();
        row.Capture("worker-1", Now);

        return row;
    }

    private static NotificationOutbox Enqueue() => NotificationOutbox.Enqueue(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        NotificationChannel.Email,
        NotificationEventTypes.AssignmentAssigned,
        JsonSerializer.SerializeToDocument(new { assignmentTitle = "Задача" }),
        "key",
        Now);
}
