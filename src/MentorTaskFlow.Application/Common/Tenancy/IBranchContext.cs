namespace MentorTaskFlow.Application.Common.Tenancy;

/// <summary>
/// The effective scope computed for one request (<c>TEN-012a</c>, TZ 38.3).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EffectiveOrganizationId"/> is <b>always</b> taken from the authenticated principal. No
/// input — body, query string, header or route — can change it. That rule has no exceptions
/// (<c>TEN-030a</c>).
/// </para>
/// <para>
/// <see cref="EffectiveBranchId"/> comes from the principal for Branch Admin, Lead and Mentor. Only an
/// Organization Admin may narrow it, via <c>X-MTF-Branch-Id</c>, and only within their own
/// organization (<c>TEN-031a</c>).
/// </para>
/// </remarks>
public interface IBranchContext
{
    Guid EffectiveOrganizationId { get; }

    /// <summary>Null in the all-branches read context.</summary>
    Guid? EffectiveBranchId { get; }

    /// <summary>
    /// Aggregate read mode across every branch of the organization. Mutations in this mode are
    /// forbidden always: such an operation has no unambiguous rollback and cannot be represented by a
    /// single AuditLog row (<c>TEN-034</c>).
    /// </summary>
    bool IsAllBranchesReadContext { get; }

    /// <summary>True only for an Organization Admin.</summary>
    bool CanOverrideBranch { get; }

    /// <summary>
    /// Returns the branch a mutation must be attributed to, or throws.
    /// </summary>
    /// <remarks>
    /// Callers performing a branch-scoped write use this instead of reading
    /// <see cref="EffectiveBranchId"/> and null-checking: forgetting the check would silently let an
    /// Organization Admin write without having chosen a branch, which is the failure
    /// <c>BRANCH_CONTEXT_REQUIRED</c> exists to prevent (<c>TEN-033</c>).
    /// </remarks>
    Guid RequireBranchForMutation();
}
