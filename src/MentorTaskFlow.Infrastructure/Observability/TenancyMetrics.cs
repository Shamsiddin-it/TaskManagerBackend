using System.Diagnostics.Metrics;

namespace MentorTaskFlow.Infrastructure.Observability;

/// <summary>
/// Counters of the tenancy model (TZ 30.4).
/// </summary>
/// <remarks>
/// <para>
/// High-cardinality labels — <c>userId</c>, <c>assignmentId</c>, email — are forbidden
/// (<c>OBS-010</c>): besides degrading the collector, <c>/metrics</c> would become a channel for
/// disclosing personal data. Only <c>organization</c>, <c>branch</c> and <c>category</c> are allowed,
/// and even those are omitted where the counter does not need them.
/// </para>
/// <para>
/// A rising <see cref="CrossScopeReferenceRejected"/> is classified as Critical: in a correct system
/// the counter stays at zero, so any increase means a defect or an attempt to cross the isolation
/// boundary (<c>TEN-026</c>).
/// </para>
/// </remarks>
public sealed class TenancyMetrics
{
    public const string MeterName = "MentorTaskFlow.Tenancy";

    private readonly Counter<long> _branchScopeDenied;
    private readonly Counter<long> _branchContextMissing;
    private readonly Counter<long> _crossScopeReferenceRejected;

    public TenancyMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _branchScopeDenied = meter.CreateCounter<long>(
            "branch_scope_denied_total",
            description: "Requests rejected with SCOPE_OVERRIDE_FORBIDDEN (TEN-032).");

        _branchContextMissing = meter.CreateCounter<long>(
            "branch_context_missing_total",
            description: "Branch-scoped mutations attempted without X-MTF-Branch-Id (TEN-033).");

        _crossScopeReferenceRejected = meter.CreateCounter<long>(
            "cross_scope_reference_rejected_total",
            description: "Attempts to link objects across organizations or branches (TEN-026).");
    }

    public void RecordBranchScopeDenied() => _branchScopeDenied.Add(1);

    public void RecordBranchContextMissing() => _branchContextMissing.Add(1);

    public void RecordCrossScopeReferenceRejected(string source) =>
        _crossScopeReferenceRejected.Add(1, new KeyValuePair<string, object?>("source", source));
}
