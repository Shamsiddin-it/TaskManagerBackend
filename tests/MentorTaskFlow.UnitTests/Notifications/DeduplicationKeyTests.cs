using MentorTaskFlow.Domain.Notifications;

namespace MentorTaskFlow.UnitTests.Notifications;

/// <summary>The deduplication key template (TZ 18.5).</summary>
public sealed class DeduplicationKeyTests
{
    private static readonly Guid Organization = Guid.Parse("019f0000-0000-7000-8000-000000000001");
    private static readonly Guid HeadOffice = Guid.Parse("019f0000-0000-7000-8000-000000000002");
    private static readonly Guid Khujand = Guid.Parse("019f0000-0000-7000-8000-000000000003");
    private static readonly Guid Entity = Guid.Parse("019f0000-0000-7000-8000-000000000004");

    [Fact]
    public void The_key_follows_the_template()
    {
        var key = DeduplicationKey.Build(
            Organization,
            HeadOffice,
            NotificationEventTypes.AssignmentAssigned,
            Entity,
            NotificationChannel.Email,
            "7");

        key.ShouldBe(
            $"{Organization:N}:{HeadOffice:N}:{NotificationEventTypes.AssignmentAssigned}:{Entity:N}:Email:7");
    }

    /// <summary>
    /// <c>TEN-043</c> and <c>TEST-TEN-019</c>: the same event for the same-named category of two
    /// branches must not collide. Without the branch in the key one branch's notification would
    /// <b>silently suppress</b> the other's — a defect that never surfaces as an error.
    /// </summary>
    [Fact]
    public void Two_branches_raising_the_same_event_produce_different_keys()
    {
        var head = DeduplicationKey.Build(
            Organization, HeadOffice, NotificationEventTypes.CategoryWithoutLead, Entity, NotificationChannel.Email);

        var khujand = DeduplicationKey.Build(
            Organization, Khujand, NotificationEventTypes.CategoryWithoutLead, Entity, NotificationChannel.Email);

        head.ShouldNotBe(khujand);
    }

    /// <summary>
    /// Under the <c>Both</c> policy one event becomes two rows. A key without the channel would make
    /// the second collide with the first, so every recipient would get exactly one of the two channels
    /// — chosen by insertion order.
    /// </summary>
    [Fact]
    public void The_two_channels_of_one_event_produce_different_keys()
    {
        var email = DeduplicationKey.Build(
            Organization, HeadOffice, NotificationEventTypes.AssignmentAssigned, Entity, NotificationChannel.Email);

        var telegram = DeduplicationKey.Build(
            Organization, HeadOffice, NotificationEventTypes.AssignmentAssigned, Entity, NotificationChannel.Telegram);

        email.ShouldNotBe(telegram);
    }

    /// <summary>An organization-level alert has no branch, and the literal says so (<c>TEN-042</c>).</summary>
    [Fact]
    public void An_organization_level_event_uses_the_org_literal() =>
        DeduplicationKey.Build(
                Organization,
                branchId: null,
                NotificationEventTypes.BranchWithoutAdmin,
                Entity,
                NotificationChannel.Email)
            .ShouldContain($":{DeduplicationKey.OrganizationLevel}:");

    [Fact]
    public void An_omitted_discriminator_leaves_no_trailing_separator() =>
        DeduplicationKey.Build(
                Organization, HeadOffice, NotificationEventTypes.AssignmentAssigned, Entity, NotificationChannel.Email)
            .ShouldNotEndWith(":");

    /// <summary>
    /// The longest key the template can produce must fit the column, or a legitimate notification
    /// fails validation instead of being sent.
    /// </summary>
    [Fact]
    public void The_longest_realistic_key_fits_the_column()
    {
        var longest = ChannelPolicies.KnownEventTypes.MaxBy(e => e.Length)!;

        // The widest discriminator in use: an ISO-8601 instant plus a recipient identifier.
        var key = DeduplicationKey.Build(
            Organization,
            HeadOffice,
            longest,
            Entity,
            NotificationChannel.Telegram,
            $"{DateTimeOffset.UtcNow:O}:{Guid.CreateVersion7():N}");

        key.Length.ShouldBeLessThanOrEqualTo(NotificationOutbox.DeduplicationKeyMaxLength);
    }
}
