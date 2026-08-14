namespace MentorTaskFlow.Domain.Notifications;

/// <summary>
/// The deduplication key of <c>NTF-015</c>.
/// </summary>
/// <remarks>
/// <para>
/// The template is <c>{organizationId}:{branchId|"org"}:{eventType}:{entityId}:{channel}:{discriminator}</c>,
/// and every segment earns its place.
/// </para>
/// <para>
/// The tenant prefix is not redundant (<c>TEN-043</c>): without it, <c>category-no-lead</c> for the
/// <c>C#</c> category of the head office and the same event for the <c>C#</c> category of another
/// branch would produce identical keys, collide on <c>ux_notification_outbox_dedup</c>, and one
/// branch's notification would <b>silently suppress</b> the other's — the most dangerous kind of
/// defect, because it never surfaces as an error.
/// </para>
/// <para>
/// The channel is equally load-bearing. Under the <c>Both</c> policy one event produces two rows, and
/// a key without the channel would make the second collide with the first — so every recipient would
/// get exactly one of the two channels, chosen by insertion order.
/// </para>
/// </remarks>
public static class DeduplicationKey
{
    public const string OrganizationLevel = "org";

    public static string Build(
        Guid organizationId,
        Guid? branchId,
        string eventType,
        Guid entityId,
        NotificationChannel channel,
        string? discriminator = null)
    {
        var scope = branchId is { } branch ? branch.ToString("N") : OrganizationLevel;

        var key = $"{organizationId:N}:{scope}:{eventType}:{entityId:N}:{channel}";

        return string.IsNullOrEmpty(discriminator) ? key : $"{key}:{discriminator}";
    }

    /// <summary>
    /// For alerts that have no entity of their own — the hourly dead-letter digest, for one
    /// (<c>NTF-022</c>).
    /// </summary>
    public static string BuildSystem(
        Guid organizationId,
        string eventType,
        NotificationChannel channel,
        string discriminator) =>
        $"{organizationId:N}:{OrganizationLevel}:{eventType}:{channel}:{discriminator}";
}
