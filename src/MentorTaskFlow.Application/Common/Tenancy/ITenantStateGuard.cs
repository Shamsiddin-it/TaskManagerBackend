namespace MentorTaskFlow.Application.Common.Tenancy;

/// <summary>
/// Refuses writes inside a deactivated organization, branch or category (TZ 9.3).
/// </summary>
/// <remarks>
/// <para>
/// The three checks run top down and the <b>first</b> violation decides the answer:
/// <c>ORGANIZATION_INACTIVE</c> → <c>BRANCH_INACTIVE</c> → <c>CATEGORY_INACTIVE</c> (<c>TEN-008</c>).
/// Order matters for more than tidiness: a category inside a deactivated branch is itself unusable,
/// and reporting the category would send an administrator to fix the wrong thing.
/// </para>
/// <para>
/// Reads are unaffected. A deactivated branch keeps historical reads working — its users still sign
/// in and see their data — and only mutations are refused (<c>BRN-031</c>, <c>CAT-012</c>). The one
/// exception is a deactivated organization, where even reading answers 403 (<c>ORG-007</c>).
/// </para>
/// </remarks>
public interface ITenantStateGuard
{
    /// <summary>
    /// Throws unless the whole chain down to <paramref name="categoryId"/> is active.
    /// </summary>
    /// <param name="branchId">The branch a write is attributed to; null checks the organization only.</param>
    /// <param name="categoryId">Optional category, checked last.</param>
    Task EnsureWritableAsync(Guid? branchId, Guid? categoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Throws when the organization itself is deactivated (<c>ORG-007</c>).
    /// </summary>
    /// <remarks>
    /// Separate because it applies to reads as well, and because deactivating an organization is an
    /// operational act performed by provisioning rather than a product feature.
    /// </remarks>
    Task EnsureOrganizationActiveAsync(CancellationToken cancellationToken);
}
