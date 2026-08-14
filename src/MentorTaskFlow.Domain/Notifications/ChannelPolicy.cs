namespace MentorTaskFlow.Domain.Notifications;

/// <summary>Which channels an event uses (TZ 18.1).</summary>
public enum ChannelPolicy
{
    /// <summary>A row in each channel.</summary>
    Both = 0,

    /// <summary>
    /// Telegram, falling back to email only when the recipient has no binding.
    /// </summary>
    /// <remarks>
    /// The fallback is the point of the policy: without it a person who never connected Telegram would
    /// silently receive no reminders at all (<c>NTF-002</c>).
    /// </remarks>
    TelegramPreferred = 1,

    EmailOnly = 2,
}

/// <summary>
/// The channel policy of each event type (TZ 18.2).
/// </summary>
/// <remarks>
/// Kept as one table so the policy cannot drift per call site. Administrative and system events are
/// <see cref="ChannelPolicy.EmailOnly"/> throughout: they are addressed to people acting in an
/// administrative capacity, and a chat message is not where an audit-relevant alert belongs.
/// </remarks>
public static class ChannelPolicies
{
    private static readonly IReadOnlyDictionary<string, ChannelPolicy> ByEventType =
        new Dictionary<string, ChannelPolicy>(StringComparer.Ordinal)
        {
            [NotificationEventTypes.AssignmentAssigned] = ChannelPolicy.Both,
            [NotificationEventTypes.AssignmentSuggested] = ChannelPolicy.TelegramPreferred,
            [NotificationEventTypes.AssignmentReassigned] = ChannelPolicy.Both,
            [NotificationEventTypes.SubmissionUploaded] = ChannelPolicy.Both,
            [NotificationEventTypes.LateSubmissionUploaded] = ChannelPolicy.Both,
            [NotificationEventTypes.ReviewApproved] = ChannelPolicy.Both,
            [NotificationEventTypes.ReviewNeedsRework] = ChannelPolicy.Both,
            [NotificationEventTypes.DeadlineReminder] = ChannelPolicy.TelegramPreferred,
            [NotificationEventTypes.AssignmentOverdue] = ChannelPolicy.Both,
            [NotificationEventTypes.AssignmentCancelled] = ChannelPolicy.Both,

            [NotificationEventTypes.SchedulerNoActiveMentor] = ChannelPolicy.EmailOnly,
            [NotificationEventTypes.CategoryWithoutLead] = ChannelPolicy.EmailOnly,

            [NotificationEventTypes.BranchDeactivated] = ChannelPolicy.Both,
            [NotificationEventTypes.BranchActivated] = ChannelPolicy.EmailOnly,
            [NotificationEventTypes.UserBranchChanged] = ChannelPolicy.Both,

            [NotificationEventTypes.BranchWithoutAdmin] = ChannelPolicy.EmailOnly,
            [NotificationEventTypes.OrganizationSystemAlert] = ChannelPolicy.EmailOnly,
            [NotificationEventTypes.NotificationDeadLetter] = ChannelPolicy.EmailOnly,

            // The link is the whole message and it must reach a mailbox, not a chat the person has not
            // connected yet — they cannot have connected it before their first sign-in (AUTH-020).
            [NotificationEventTypes.UserInvitation] = ChannelPolicy.EmailOnly,
        };

    /// <summary>
    /// The policy for an event type.
    /// </summary>
    /// <remarks>
    /// An unknown type is a programming error, not a runtime condition: the catalog is closed and
    /// <c>NotificationEventTypeTests</c> asserts every constant appears here exactly once.
    /// </remarks>
    public static ChannelPolicy For(string eventType) =>
        ByEventType.TryGetValue(eventType, out var policy)
            ? policy
            : throw new ArgumentOutOfRangeException(
                nameof(eventType),
                eventType,
                "Событие отсутствует в каталоге политик каналов (18.2).");

    public static IReadOnlyCollection<string> KnownEventTypes => (IReadOnlyCollection<string>)ByEventType.Keys;
}
