namespace MentorTaskFlow.Infrastructure.Persistence;

/// <summary>
/// Per-request tenant scope consumed by the EF Core global query filters.
/// </summary>
/// <remarks>
/// <para>
/// The default is <b>fail-closed</b>: with nothing set, <see cref="OrganizationId"/> is null and the
/// filters match no rows. An unauthenticated or mis-wired request therefore sees an empty database
/// rather than everything — the opposite default would turn a missing middleware registration into a
/// cross-tenant leak.
/// </para>
/// <para>
/// <see cref="Suppress"/> exists for the registered system tasks only — bootstrap, retention, orphan
/// cleanup, recovery of stuck outbox rows. Each of them applies scope itself and records
/// <c>OrganizationId</c>/<c>BranchId</c> in the AuditLog; the list of exceptions lives in code as the
/// single source of truth and is checked by <c>TEST-SEC-022</c> (<c>SEC-031</c>).
/// </para>
/// <para>
/// This is never the only protection. <c>SEC-002</c> and <c>SEC-030</c> require an explicit scope
/// condition in every handler as well: a query filter is silently dropped by
/// <c>IgnoreQueryFilters()</c>, does not apply to raw SQL, and does not guard writes at all.
/// </para>
/// </remarks>
public sealed class TenantFilterState
{
    public Guid? OrganizationId { get; private set; }

    /// <summary>Null in the all-branches read context, where only the organization narrows the query.</summary>
    public Guid? BranchId { get; private set; }

    public bool IsSuppressed { get; private set; }

    public void SetScope(Guid organizationId, Guid? branchId)
    {
        OrganizationId = organizationId;
        BranchId = branchId;
        IsSuppressed = false;
    }

    /// <summary>Disables the filters for a registered system task. Never called from a request path.</summary>
    public void Suppress()
    {
        IsSuppressed = true;
        OrganizationId = null;
        BranchId = null;
    }
}
