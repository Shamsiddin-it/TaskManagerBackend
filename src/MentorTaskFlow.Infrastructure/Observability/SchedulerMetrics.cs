using System.Diagnostics.Metrics;

namespace MentorTaskFlow.Infrastructure.Observability;

/// <summary>
/// Counters of the background jobs (TZ 30.4).
/// </summary>
/// <remarks>
/// Labels are limited to <c>organization</c>, <c>branch</c> and <c>category</c> (<c>TEN-056</c>). A
/// user or assignment identifier as a label would both blow up cardinality and turn <c>/metrics</c>
/// into a disclosure channel (<c>OBS-010</c>).
/// </remarks>
public sealed class SchedulerMetrics
{
    public const string MeterName = "MentorTaskFlow.Scheduler";

    private readonly Counter<long> _suggestionsCreated;
    private readonly Counter<long> _duplicatesSkipped;
    private readonly Counter<long> _skipped;
    private readonly Counter<long> _markedOverdue;
    private readonly Counter<long> _remindersSent;

    public SchedulerMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _suggestionsCreated = meter.CreateCounter<long>(
            "scheduler_suggestions_created_total",
            description: "Suggestions produced by auto-generation.");

        _duplicatesSkipped = meter.CreateCounter<long>(
            "scheduler_duplicate_skipped_total",
            description: "Inserts skipped by the idempotency key on a repeated run (SCH-010).");

        _skipped = meter.CreateCounter<long>(
            "scheduler_skipped_total",
            description: "Generation stopped at an inactive link of the chain (TEN-050).");

        _markedOverdue = meter.CreateCounter<long>(
            "scheduler_marked_overdue_total",
            description: "Assignments moved to Overdue.");

        _remindersSent = meter.CreateCounter<long>(
            "scheduler_reminders_total",
            description: "Deadline reminders enqueued.");
    }

    public void SuggestionCreated(Guid branchId) => _suggestionsCreated.Add(1, Branch(branchId));

    public void DuplicateSkipped(Guid branchId) => _duplicatesSkipped.Add(1, Branch(branchId));

    public void Skipped(string reason) =>
        _skipped.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void MarkedOverdue(long count) => _markedOverdue.Add(count);

    public void ReminderSent() => _remindersSent.Add(1);

    private static KeyValuePair<string, object?> Branch(Guid branchId) => new("branch", branchId.ToString("N"));
}
