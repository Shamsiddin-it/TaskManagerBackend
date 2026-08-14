using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Notifications;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.IntegrationTests.Notifications;

/// <summary>The outbox worker: claim, deliver, retry, dead-letter (TZ 18.4).</summary>
[Collection(PostgresCollection.Name)]
public sealed class OutboxDeliveryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Seeded = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private Guid _organizationId;
    private Guid _headOfficeId;
    private Guid _khujandId;
    private Guid _headCategoryId;
    private Guid _khujandCategoryId;
    private Guid _mentorId;
    private Guid _organizationAdminId;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // -----------------------------------------------------------------
    // Delivery
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_pending_row_is_delivered_and_marked_sent()
    {
        await EnqueueAsync();

        var sender = new RecordingSender();
        (await DispatchAsync(sender)).ShouldBe(1);

        sender.Sent.ShouldHaveSingleItem().EventType.ShouldBe(NotificationEventTypes.AssignmentAssigned);

        var row = await SingleRowAsync();
        row.Status.ShouldBe(NotificationStatus.Sent);
        row.Attempts.ShouldBe(1);
        row.SentAt.ShouldNotBeNull();
        row.ProviderMessageId.ShouldBe("recorded");
    }

    /// <summary>The message carries the recipient resolved at delivery, not stored in the row.</summary>
    [Fact]
    public async Task The_recipient_is_resolved_at_delivery_time()
    {
        await EnqueueAsync();

        var sender = new RecordingSender();
        await DispatchAsync(sender);

        var message = sender.Sent.ShouldHaveSingleItem();
        message.RecipientEmail.ShouldBe("mentor-head@mentortaskflow.test");
        message.RecipientFullName.ShouldBe("Ментор");
    }

    /// <summary>A row already sent is not claimed again — the claim looks only at <c>Pending</c>.</summary>
    [Fact]
    public async Task A_delivered_row_is_not_claimed_twice()
    {
        await EnqueueAsync();

        await DispatchAsync(new RecordingSender());

        var second = new RecordingSender();
        (await DispatchAsync(second)).ShouldBe(0);
        second.Sent.ShouldBeEmpty();
    }

    // -----------------------------------------------------------------
    // Retry and the dead letter (NTF-013)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_temporary_failure_is_rescheduled_a_minute_later()
    {
        await EnqueueAsync();

        var clock = new FixedClock(Seeded);
        await DispatchAsync(new FailingSender(DeliveryFailure.Temporary), clock);

        var row = await SingleRowAsync();
        row.Status.ShouldBe(NotificationStatus.Pending);
        row.Attempts.ShouldBe(1);
        row.NextAttemptAt.ShouldBe(Seeded.AddMinutes(1), TimeSpan.FromSeconds(1));
        row.LastError.ShouldNotBeNull();
    }

    /// <summary>A row not yet due is passed over: the backoff is a schedule, not a suggestion.</summary>
    [Fact]
    public async Task A_rescheduled_row_is_not_retried_before_its_time()
    {
        await EnqueueAsync();

        var clock = new FixedClock(Seeded);
        await DispatchAsync(new FailingSender(DeliveryFailure.Temporary), clock);

        (await DispatchAsync(new RecordingSender(), clock)).ShouldBe(0);

        clock.Now = Seeded.AddMinutes(2);
        (await DispatchAsync(new RecordingSender(), clock)).ShouldBe(1);
    }

    [Fact]
    public async Task A_permanent_failure_dead_letters_immediately()
    {
        await EnqueueAsync();

        await DispatchAsync(new FailingSender(DeliveryFailure.Permanent));

        var row = await SingleRowAsync();
        row.Status.ShouldBe(NotificationStatus.DeadLetter);
        row.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Five_temporary_failures_end_in_the_dead_letter()
    {
        await EnqueueAsync();

        var clock = new FixedClock(Seeded);

        for (var attempt = 0; attempt < NotificationOutbox.MaxAttempts; attempt++)
        {
            await DispatchAsync(new FailingSender(DeliveryFailure.Temporary), clock);
            clock.Now = clock.Now.AddHours(12);
        }

        var row = await SingleRowAsync();
        row.Status.ShouldBe(NotificationStatus.DeadLetter);
        row.Attempts.ShouldBe(NotificationOutbox.MaxAttempts);
    }

    // -----------------------------------------------------------------
    // Lease recovery (NTF-012)
    // -----------------------------------------------------------------

    /// <summary>
    /// A process killed mid-send leaves rows in <c>Processing</c>, which nothing else recovers.
    /// </summary>
    [Fact]
    public async Task An_expired_lease_returns_the_row_to_the_queue()
    {
        await EnqueueAsync();

        var clock = new FixedClock(Seeded);

        // Claim without delivering: exactly what a crash between the two looks like.
        await using (var context = postgres.CreateContext(suppressTenantFilter: true))
        {
            var row = await context.NotificationOutbox.SingleAsync();
            row.Capture("worker-that-died", clock.Now);
            await context.SaveChangesAsync();
        }

        clock.Now = Seeded.Add(NotificationOutbox.LeaseDuration).AddMinutes(1);

        (await RecoverAsync(clock)).ShouldBe(1);

        var recovered = await SingleRowAsync();
        recovered.Status.ShouldBe(NotificationStatus.Pending);
        recovered.LockedBy.ShouldBeNull();

        (await DispatchAsync(new RecordingSender(), clock)).ShouldBe(1);
    }

    [Fact]
    public async Task A_live_lease_is_left_alone()
    {
        await EnqueueAsync();

        var clock = new FixedClock(Seeded);

        await using (var context = postgres.CreateContext(suppressTenantFilter: true))
        {
            var row = await context.NotificationOutbox.SingleAsync();
            row.Capture("worker-1", clock.Now);
            await context.SaveChangesAsync();
        }

        clock.Now = Seeded.AddMinutes(1);

        (await RecoverAsync(clock)).ShouldBe(0);
        (await SingleRowAsync()).Status.ShouldBe(NotificationStatus.Processing);
    }

    // -----------------------------------------------------------------
    // Dead-letter alerting (NTF-020…NTF-022)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_dead_letter_raises_an_alert_to_the_organization_admin()
    {
        await EnqueueAsync();

        await DispatchAsync(new FailingSender(DeliveryFailure.Permanent));

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var alert = await context.NotificationOutbox
            .SingleAsync(n => n.EventType == NotificationEventTypes.NotificationDeadLetter);

        alert.UserId.ShouldBe(_organizationAdminId);
        alert.IsSystemAlert.ShouldBeTrue();

        // TEN-042: the failure concerns the organization, not any one branch.
        alert.BranchId.ShouldBeNull();
        alert.Payload.RootElement.GetProperty("failedCount").GetInt32().ShouldBe(1);
    }

    /// <summary>
    /// <c>NTF-022</c>: one digest an hour, whatever the volume. A hundred dead letters must not become
    /// a hundred emails through the provider that is already failing.
    /// </summary>
    [Fact]
    public async Task Several_dead_letters_in_one_hour_raise_one_alert()
    {
        await EnqueueAsync(discriminator: "1");
        await EnqueueAsync(discriminator: "2");

        var clock = new FixedClock(Seeded);
        await DispatchAsync(new FailingSender(DeliveryFailure.Permanent), clock);

        clock.Now = Seeded.AddMinutes(10);
        await EnqueueAsync(discriminator: "3");
        await DispatchAsync(new FailingSender(DeliveryFailure.Permanent), clock);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.NotificationOutbox
                .CountAsync(n => n.EventType == NotificationEventTypes.NotificationDeadLetter))
            .ShouldBe(1);
    }

    /// <summary>
    /// <c>NTF-021</c>: the chain stops at the first level. Without this an unreachable provider would
    /// generate an alert about the failed alert, and then an alert about that, without end.
    /// </summary>
    [Fact]
    public async Task A_failed_alert_raises_no_further_alert()
    {
        await EnqueueAsync();

        var clock = new FixedClock(Seeded);
        await DispatchAsync(new FailingSender(DeliveryFailure.Permanent), clock);

        // The alert itself now fails, an hour later so its own deduplication window has moved on.
        clock.Now = Seeded.AddHours(2);
        await DispatchAsync(new FailingSender(DeliveryFailure.Permanent), clock);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var alerts = await context.NotificationOutbox
            .Where(n => n.EventType == NotificationEventTypes.NotificationDeadLetter)
            .ToListAsync();

        alerts.ShouldHaveSingleItem().Status.ShouldBe(NotificationStatus.DeadLetter);
    }

    // -----------------------------------------------------------------
    // Channel policy and deduplication
    // -----------------------------------------------------------------

    /// <summary><c>NTF-001</c>: no Telegram row without a binding, and that is not an error.</summary>
    [Fact]
    public async Task A_both_policy_event_produces_email_only_without_a_telegram_binding()
    {
        await EnqueueAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.NotificationOutbox.Select(n => n.Channel).ToListAsync())
            .ShouldBe([NotificationChannel.Email]);
    }

    [Fact]
    public async Task A_both_policy_event_produces_two_rows_for_a_bound_recipient()
    {
        await BindTelegramAsync();
        await EnqueueAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.NotificationOutbox.Select(n => n.Channel).OrderBy(c => c).ToListAsync())
            .ShouldBe([NotificationChannel.Email, NotificationChannel.Telegram]);
    }

    /// <summary>
    /// <c>NTF-002</c>: the fallback is the point of <c>TelegramPreferred</c>. Without it a person who
    /// never connected Telegram would silently receive no reminders at all.
    /// </summary>
    [Fact]
    public async Task A_telegram_preferred_event_falls_back_to_email()
    {
        await EnqueueAsync(NotificationEventTypes.DeadlineReminder);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.NotificationOutbox.Select(n => n.Channel).ToListAsync())
            .ShouldBe([NotificationChannel.Email]);
    }

    [Fact]
    public async Task A_telegram_preferred_event_uses_telegram_when_bound()
    {
        await BindTelegramAsync();
        await EnqueueAsync(NotificationEventTypes.DeadlineReminder);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.NotificationOutbox.Select(n => n.Channel).ToListAsync())
            .ShouldBe([NotificationChannel.Telegram]);
    }

    /// <summary>
    /// <c>TEST-TEN-019</c>: the same event raised in two branches must produce two notifications. A key
    /// without the tenant prefix would let one branch's message silently suppress the other's.
    /// </summary>
    [Fact]
    public async Task The_same_event_in_two_branches_produces_two_notifications()
    {
        var khujandMentorId = await KhujandMentorIdAsync();

        await EnqueueAsync(branchId: _headOfficeId, categoryId: _headCategoryId, recipientId: _mentorId);
        await EnqueueAsync(branchId: _khujandId, categoryId: _khujandCategoryId, recipientId: khujandMentorId);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var rows = await context.NotificationOutbox.ToListAsync();

        rows.Count.ShouldBe(2);
        rows.Select(n => n.BranchId).ShouldBe([_headOfficeId, _khujandId], ignoreOrder: true);
        rows.Select(n => n.DeduplicationKey).Distinct().Count().ShouldBe(2);
    }

    /// <summary>The same event twice on one object is enqueued once (18.5).</summary>
    [Fact]
    public async Task A_repeated_event_is_enqueued_once()
    {
        await EnqueueAsync();
        await EnqueueAsync();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.NotificationOutbox.CountAsync()).ShouldBe(1);
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    private static readonly Guid EntityId = Guid.Parse("019f0000-0000-7000-8000-00000000000a");

    private async Task EnqueueAsync(
        string eventType = NotificationEventTypes.AssignmentAssigned,
        string? discriminator = null,
        Guid? branchId = null,
        Guid? categoryId = null,
        Guid? recipientId = null)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var writer = new OutboxWriter(context, new StubBranchContext(_organizationId, _headOfficeId), Metrics(), new FixedClock(Seeded));

        await writer.EnqueueSystemAsync(
            new OutboxEntry
            {
                RecipientUserId = recipientId ?? _mentorId,
                EventType = eventType,
                EntityId = EntityId,
                Discriminator = discriminator,
                CategoryId = categoryId ?? _headCategoryId,
                Payload = JsonSerializer.SerializeToDocument(new { assignmentTitle = "Задача" }),
            },
            _organizationId,
            branchId ?? _headOfficeId,
            CancellationToken.None);

        await context.SaveChangesAsync();
    }

    private async Task<int> DispatchAsync(INotificationSender sender, FixedClock? clock = null)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        return await Dispatcher(context, sender, clock).DispatchAsync("worker-test", CancellationToken.None);
    }

    private async Task<int> RecoverAsync(FixedClock clock)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        return await Dispatcher(context, new RecordingSender(), clock)
            .RecoverExpiredLeasesAsync(CancellationToken.None);
    }

    private static OutboxDispatcher Dispatcher(
        MentorTaskFlowDbContext context,
        INotificationSender sender,
        FixedClock? clock) =>
        new(
            context,
            [sender],
            Metrics(),
            Options.Create(new NotificationOptions { SmtpHost = "localhost", BatchSize = 50 }),
            NullLogger<OutboxDispatcher>.Instance,
            clock ?? new FixedClock(Seeded));

    private static NotificationMetrics Metrics() =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());

    private async Task<NotificationOutbox> SingleRowAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        return await context.NotificationOutbox
            .Where(n => n.EventType != NotificationEventTypes.NotificationDeadLetter)
            .SingleAsync();
    }

    private async Task BindTelegramAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var mentor = await context.Users.SingleAsync(u => u.Id == _mentorId);
        mentor.BindTelegram("123456789", Seeded);

        await context.SaveChangesAsync();
    }

    private async Task<Guid> KhujandMentorIdAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        return await context.Users
            .Where(u => u.Email == "mentor-khujand@mentortaskflow.test")
            .Select(u => u.Id)
            .SingleAsync();
    }

    private sealed class RecordingSender : INotificationSender
    {
        public List<NotificationMessage> Sent { get; } = [];

        public NotificationChannel Channel => NotificationChannel.Email;

        public Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);

            return Task.FromResult(DeliveryResult.Success("recorded"));
        }
    }

    private sealed class FailingSender(DeliveryFailure failure) : INotificationSender
    {
        public NotificationChannel Channel => NotificationChannel.Email;

        public Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken) =>
            Task.FromResult(failure is DeliveryFailure.Permanent
                ? DeliveryResult.Fatal("SMTP 5.1.1 unknown mailbox")
                : DeliveryResult.Retryable("таймаут SMTP"));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; set; } = now;

        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubBranchContext(Guid organizationId, Guid branchId) : Application.Common.Tenancy.IBranchContext
    {
        public Guid EffectiveOrganizationId => organizationId;

        public Guid? EffectiveBranchId => branchId;

        public bool IsAllBranchesReadContext => false;

        public bool CanOverrideBranch => false;

        public Guid RequireBranchForMutation() => branchId;
    }

    private async Task SeedAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Seeded);
        var khujand = Branch.Create(organization.Id, "Филиал Худжанд", "KHJ", null, "Asia/Dushanbe", Seeded);
        context.Branches.AddRange(headOffice, khujand);

        var headCategory = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        var khujandCategory = Category.Create(organization.Id, khujand.Id, "C#", null, Seeded);
        context.Categories.AddRange(headCategory, khujandCategory);

        context.CategorySettings.AddRange(
            CategorySettings.CreateDefault(headCategory, headOffice.TimeZoneId, Seeded),
            CategorySettings.CreateDefault(khujandCategory, khujand.TimeZoneId, Seeded));

        var admin = User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded);
        var mentor = User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded);
        var khujandMentor = User.CreateMentor(organization.Id, khujand.Id, khujandCategory.Id, "Ментор Худжанда", "mentor-khujand@mentortaskflow.test", Seeded);

        context.Users.AddRange(admin, mentor, khujandMentor);
        await context.SaveChangesAsync();

        _organizationId = organization.Id;
        _headOfficeId = headOffice.Id;
        _khujandId = khujand.Id;
        _headCategoryId = headCategory.Id;
        _khujandCategoryId = khujandCategory.Id;
        _mentorId = mentor.Id;
        _organizationAdminId = admin.Id;
    }
}
