namespace MentorTaskFlow.Domain.Tenancy;

/// <summary>
/// The administrative reach of <see cref="UserRole.Admin"/> (<c>TEN-012</c>).
/// </summary>
/// <remarks>
/// Defined only for <see cref="UserRole.Admin"/>: <c>Role='Admin' ⇒ AdminScope IS NOT NULL</c> and
/// <c>Role&lt;&gt;'Admin' ⇒ AdminScope IS NULL</c>, guaranteed by the CHECK constraint
/// <c>ck_users_role_admin_scope</c> rather than by service code alone (<c>TEN-013</c>).
/// </remarks>
public enum AdminScope
{
    /// <summary>Whole organization. <c>BranchId</c> and <c>CategoryId</c> are null.</summary>
    Organization = 0,

    /// <summary>A single branch. <c>BranchId</c> is required, <c>CategoryId</c> is null.</summary>
    Branch = 1,
}
