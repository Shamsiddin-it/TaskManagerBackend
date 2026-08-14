using System.Diagnostics.Metrics;

namespace MentorTaskFlow.Infrastructure.Observability;

/// <summary>
/// Counters of the notification pipeline (TZ 30.2, 30.3).
/// </summary>
/// <remarks>
/// <c>NTF-023</c> makes these the <b>primary</b> way a dead letter becomes known, not email: an
/// unreachable mail provider is exactly the condition that produces dead letters, so learning about
/// them by mail is a plan that fails when it is needed. Labels stay low-cardinality — event type and
/// channel only, never a recipient (<c>OBS-010</c>).
/// </remarks>
public sealed class NotificationMetrics
{
    public const string MeterName = "MentorTaskFlow.Notifications";

    private readonly Counter<long> _sent;
    private readonly Counter<long> _skipped;
    private readonly Counter<long> _retried;
    private readonly Counter<long> _deadLettered;

    public NotificationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _sent = meter.CreateCounter<long>(
            "notification_sent_total",
            description: "Notifications confirmed by a provider.");

        _skipped = meter.CreateCounter<long>(
            "notification_skipped_total",
            description: "Channel rows not created — a recipient with no Telegram binding (NTF-001).");

        _retried = meter.CreateCounter<long>(
            "notification_retry_total",
            description: "Temporary failures rescheduled by the backoff of NTF-013.");

        _deadLettered = meter.CreateCounter<long>(
            "notification_outbox_deadletter_total",
            description: "Notifications abandoned: a permanent failure or five exhausted attempts.");
    }

    public void Sent(string channel) =>
        _sent.Add(1, new KeyValuePair<string, object?>("channel", channel));

    public void SkippedTelegram(string eventType) => _skipped.Add(
        1,
        new KeyValuePair<string, object?>("channel", "telegram"),
        new KeyValuePair<string, object?>("reason", "not_bound"),
        new KeyValuePair<string, object?>("event_type", eventType));

    public void Retried(string channel) =>
        _retried.Add(1, new KeyValuePair<string, object?>("channel", channel));

    public void DeadLettered(string channel, string reason) => _deadLettered.Add(
        1,
        new KeyValuePair<string, object?>("channel", channel),
        new KeyValuePair<string, object?>("reason", reason));
}
