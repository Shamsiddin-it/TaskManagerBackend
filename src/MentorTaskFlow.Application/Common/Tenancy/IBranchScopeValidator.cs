namespace MentorTaskFlow.Application.Common.Tenancy;

/// <summary>
/// Confirms that a branch id supplied through <c>X-MTF-Branch-Id</c> belongs to the caller's
/// organization.
/// </summary>
/// <remarks>
/// Validated on <b>every</b> request rather than cached per session: the branch may have been
/// deactivated, or the administrator's contour changed, between two requests (<c>TEN-035a</c>).
/// </remarks>
public interface IBranchScopeValidator
{
    /// <summary>
    /// Returns <c>true</c> when the branch exists and belongs to <paramref name="organizationId"/>.
    /// </summary>
    /// <remarks>
    /// A branch of another organization and a non-existent branch are the same answer, and the caller
    /// turns both into 404 <c>RESOURCE_NOT_FOUND</c> — never 403. A distinct code would confirm that a
    /// foreign branch exists (<c>TEN-007</c>, <c>TEN-032a</c>).
    /// </remarks>
    Task<bool> BelongsToOrganizationAsync(Guid branchId, Guid organizationId, CancellationToken cancellationToken);
}
