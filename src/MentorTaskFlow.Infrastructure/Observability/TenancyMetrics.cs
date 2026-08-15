using System.Diagnostics.Metrics;

namespace MentorTaskFlow.Infrastructure.Observability;

/// <summary>
/// Counters of the tenancy model (TZ 30.4, <c>TEN-096</c>).
/// </summary>
/// <remarks>
/// <para>
/// High-cardinality labels — <c>userId</c>, <c>assignmentId</c>, email — are forbidden
/// (<c>OBS-010</c>): besides degrading the collector, <c>/metrics</c> would become a channel for
/// disclosing personal data. Only the names in <see cref="MetricLabels"/> are used, and the tenant
/// ones carry <c>Organization.Slug</c> and <c>Branch.Code</c> rather than UUIDs (<c>OBS-011</c>).
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

    private readonly TenantLabelResolver _labels;

    private readonly Counter<long> _branchScopeDenied;
    private readonly Counter<long> _organizationScopeDenied;
    private readonly Counter<long> _branchContextMissing;
    private readonly Counter<long> _crossScopeReferenceRejected;
    private readonly Counter<long> _branchChangeBlocked;

    public TenancyMetrics(IMeterFactory meterFactory, TenantLabelResolver labels)
    {
        _labels = labels;

        var meter = meterFactory.Create(MeterName);

        _branchScopeDenied = meter.CreateCounter<long>(
            "branch_scope_denied_total",
            description: "Requests rejected with SCOPE_OVERRIDE_FORBIDDEN (TEN-032).");

        _organizationScopeDenied = meter.CreateCounter<long>(
            "organization_scope_denied_total",
            description: "Requests rejected for naming another organization's scope (TEN-030).");

        _branchContextMissing = meter.CreateCounter<long>(
            "branch_context_missing_total",
            description: "Branch-scoped mutations attempted without X-MTF-Branch-Id (TEN-033).");

        _crossScopeReferenceRejected = meter.CreateCounter<long>(
            "cross_scope_reference_rejected_total",
            description: "Attempts to link objects across organizations or branches (TEN-026).");

        _branchChangeBlocked = meter.CreateCounter<long>(
            "branch_change_blocked_total",
            description: "Transfers refused because the user still holds unfinished work (USER-012).");
    }

    public void RecordBranchScopeDenied(Guid? organizationId = null, Guid? branchId = null) =>
        _branchScopeDenied.Add(
            1,
            new KeyValuePair<string, object?>(MetricLabels.Organization, _labels.Organization(organizationId)),
            new(MetricLabels.Branch, _labels.Branch(branchId)));

    public void RecordOrganizationScopeDenied(Guid? organizationId = null) =>
        _organizationScopeDenied.Add(
            1,
            new KeyValuePair<string, object?>(MetricLabels.Organization, _labels.Organization(organizationId)));

    public void RecordBranchContextMissing(Guid? organizationId = null) =>
        _branchContextMissing.Add(
            1,
            new KeyValuePair<string, object?>(MetricLabels.Organization, _labels.Organization(organizationId)));

    public void RecordCrossScopeReferenceRejected(string source, Guid? organizationId = null) =>
        _crossScopeReferenceRejected.Add(
            1,
            new KeyValuePair<string, object?>(MetricLabels.Organization, _labels.Organization(organizationId)),
            new(MetricLabels.Source, source));

    public void RecordBranchChangeBlocked(string reason, Guid? organizationId = null) =>
        _branchChangeBlocked.Add(
            1,
            new KeyValuePair<string, object?>(MetricLabels.Organization, _labels.Organization(organizationId)),
            new(MetricLabels.Reason, reason));
}
