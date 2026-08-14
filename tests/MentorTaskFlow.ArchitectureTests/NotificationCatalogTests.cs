using System.Reflection;
using MentorTaskFlow.Domain.Notifications;

namespace MentorTaskFlow.ArchitectureTests;

/// <summary>
/// The event catalog and its channel policies stay in step (TZ 18.1, 18.2, Приложение E).
/// </summary>
/// <remarks>
/// An event without a policy throws only when that event is first raised — possibly in production,
/// possibly months later. Comparing the two lists here turns that into a build failure.
/// </remarks>
public sealed class NotificationCatalogTests
{
    private static readonly string[] EventTypes = [.. typeof(NotificationEventTypes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!),
    ];

    [Fact]
    public void Every_event_type_has_a_channel_policy()
    {
        foreach (var eventType in EventTypes)
        {
            Should.NotThrow(() => ChannelPolicies.For(eventType), $"«{eventType}» has no channel policy (18.2).");
        }
    }

    [Fact]
    public void The_policy_table_invents_no_events() =>
        ChannelPolicies.KnownEventTypes.ShouldBeSubsetOf(EventTypes);

    /// <summary>
    /// The 19 of Приложение E, counted so an addition to one list without the other is caught.
    /// </summary>
    [Fact]
    public void The_catalog_holds_nineteen_events() => EventTypes.Length.ShouldBe(19);

    /// <summary>
    /// <c>TEN-042</c>: exactly three event types may carry a null branch, plus the invitation, whose
    /// first recipient is an Organization Admin with no branch (<c>DEPLOY-031</c>).
    /// </summary>
    [Fact]
    public void Only_organization_level_events_may_omit_a_branch()
    {
        NotificationEventTypes.OrganizationLevelEvents.ShouldBe(
            [
                NotificationEventTypes.BranchWithoutAdmin,
                NotificationEventTypes.OrganizationSystemAlert,
                NotificationEventTypes.NotificationDeadLetter,
                NotificationEventTypes.UserInvitation,
            ],
            ignoreOrder: true);
    }

    /// <summary>
    /// Administrative and system events go by mail (18.2). A chat message is not where an
    /// audit-relevant alert belongs, and the dead-letter alert in particular must not depend on a
    /// channel that may itself be the thing that failed.
    /// </summary>
    [Theory]
    [InlineData(NotificationEventTypes.BranchWithoutAdmin)]
    [InlineData(NotificationEventTypes.CategoryWithoutLead)]
    [InlineData(NotificationEventTypes.SchedulerNoActiveMentor)]
    [InlineData(NotificationEventTypes.NotificationDeadLetter)]
    [InlineData(NotificationEventTypes.OrganizationSystemAlert)]
    [InlineData(NotificationEventTypes.UserInvitation)]
    public void Administrative_events_are_email_only(string eventType) =>
        ChannelPolicies.For(eventType).ShouldBe(ChannelPolicy.EmailOnly);

    /// <summary>The two the TZ singles out as reaching a person who may not read mail promptly.</summary>
    [Theory]
    [InlineData(NotificationEventTypes.AssignmentSuggested)]
    [InlineData(NotificationEventTypes.DeadlineReminder)]
    public void The_two_telegram_preferred_events_match_the_specification(string eventType) =>
        ChannelPolicies.For(eventType).ShouldBe(ChannelPolicy.TelegramPreferred);

    /// <summary>
    /// The backoff of <c>NTF-013</c>, and one delay per permitted attempt so the last failure always
    /// has a schedule to consult before it dead-letters.
    /// </summary>
    [Fact]
    public void The_backoff_matches_the_specification()
    {
        NotificationOutbox.RetryBackoff.ShouldBe([
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(6),
        ]);

        NotificationOutbox.RetryBackoff.Count.ShouldBe(NotificationOutbox.MaxAttempts);
    }

    /// <summary>
    /// Version 2.0's <c>Failed</c> was removed as a duplicate of <c>DeadLetter</c>: two states meaning
    /// «did not arrive» produced retry logic that handled both, and handled them differently.
    /// </summary>
    [Fact]
    public void There_is_no_failed_status()
    {
        Enum.GetNames<NotificationStatus>().ShouldBe(["Pending", "Processing", "Sent", "DeadLetter"], ignoreOrder: true);
        Enum.GetNames<NotificationStatus>().ShouldNotContain("Failed");
    }
}
