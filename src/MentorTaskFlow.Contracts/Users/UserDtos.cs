using MentorTaskFlow.Contracts.Tenancy;

namespace MentorTaskFlow.Contracts.Users;

/// <summary>
/// A user as returned by the administrative endpoints (Приложение D.2).
/// </summary>
/// <remarks>
/// <c>branch</c> is populated in the all-branches read context, where two users may otherwise be
/// indistinguishable in origin (<c>TEN-073</c>). <c>hasPassword</c> replaces any exposure of the hash:
/// the interface needs to show "invitation pending", and <c>AUD-022</c> forbids the hash leaving the
/// database at all.
/// </remarks>
public sealed record UserDto(
    Guid Id,
    string FullName,
    string Email,
    string Role,
    string? AdminScope,
    Guid OrganizationId,
    Guid? BranchId,
    Guid? CategoryId,
    bool IsActive,
    bool HasPassword,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt,
    string ConcurrencyToken,
    BranchSummaryDto? Branch);

/// <summary>
/// <c>POST /users</c>.
/// </summary>
/// <remarks>
/// <para>
/// No <c>organizationId</c> and no <c>branchId</c>: both are decided by the server — from the
/// creator's claim, or from <c>X-MTF-Branch-Id</c> for an Organization Admin — and a client that
/// sends either gets 400 (<c>USER-032</c>, <c>SEC-003</c>).
/// </para>
/// <para>
/// No password field either. One is never generated, mailed or displayed; the account is created
/// without a hash and the person sets their own through a one-time link (<c>AUTH-019</c>,
/// <c>USER-001</c>).
/// </para>
/// <para>
/// <c>adminScope</c> is accepted only when an Organization Admin creates an administrator. In every
/// other request its presence is 400 <c>VALIDATION_FAILED</c> (<c>TEN-014</c>).
/// </para>
/// </remarks>
public sealed record CreateUserRequest(
    string FullName,
    string Email,
    string Role,
    string? AdminScope = null,
    Guid? CategoryId = null);

/// <summary>
/// <c>PATCH /users/{id}</c> — the only editable field is the display name.
/// </summary>
/// <remarks>
/// Role, scope and activity each have a dedicated operation, because each carries consequences a
/// generic edit would hide: token revocation, history rows, notifications (<c>API-009</c>).
/// </remarks>
public sealed record PatchUserRequest(string FullName, string ConcurrencyToken);

/// <summary>
/// <c>POST /users/{id}/change-role</c>, which also carries a change of administrative contour
/// (<c>USER-008</c>, <c>USER-033</c>).
/// </summary>
/// <remarks>
/// <c>reason</c> is mandatory, 5–500 characters: this changes someone's level of access, and an audit
/// trail that records the change without the why is of little use in a review.
/// </remarks>
public sealed record ChangeRoleRequest(
    string Role,
    string Reason,
    string ConcurrencyToken,
    string? AdminScope = null,
    Guid? BranchId = null,
    Guid? CategoryId = null);

/// <summary>Body of the activate and deactivate actions.</summary>
public sealed record UserActionRequest(string ConcurrencyToken);

/// <summary>
/// <c>POST /users/{id}/change-category</c> — a move <b>within one branch</b> (TZ 15.2).
/// </summary>
/// <remarks>
/// There is no branch field, and that is the point: a move that also changes branch is
/// <see cref="ChangeBranchRequest"/>, a different operation with a different authorisation rule. The
/// target category must belong to the user's current branch, otherwise 409
/// <c>CROSS_SCOPE_REFERENCE</c> (<c>USER-037</c>).
/// </remarks>
public sealed record ChangeCategoryRequest(Guid NewCategoryId, string Reason, string ConcurrencyToken);

/// <summary>
/// <c>POST /users/{id}/change-branch</c> — Organization Admin only (TZ 39.6).
/// </summary>
/// <remarks>
/// <c>newCategoryId</c> is mandatory for a Lead or Mentor and must be <c>null</c> for a Branch Admin:
/// the four permitted scope shapes of <c>USER-023</c> leave no other combination
/// (<c>BRN-037</c>).
/// </remarks>
public sealed record ChangeBranchRequest(
    Guid NewBranchId,
    string Reason,
    string ConcurrencyToken,
    Guid? NewCategoryId = null);

/// <summary>
/// The <c>reason</c> code carried by a blocked transfer.
/// </summary>
/// <remarks>
/// <c>BRN-039</c> restricts the body of a 409 to the identifiers of the blocking assignments and a
/// reason code — no task titles, no names, nothing else. The response exists so an administrator can
/// navigate to what is in the way, not to report on it.
/// </remarks>
public static class TransferBlockReasons
{
    /// <summary>The user still holds assignments in a non-terminal status.</summary>
    public const string ActiveAssignments = "ACTIVE_ASSIGNMENTS";

    /// <summary>The user is the active Lead of their category (<c>USER-013</c>).</summary>
    public const string ActiveLead = "ACTIVE_LEAD";
}

/// <summary>Filters for <c>GET /users</c> (<c>USER-010</c>).</summary>
public sealed record UserListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    public string? Role { get; init; }

    public Guid? CategoryId { get; init; }

    public bool? IsActive { get; init; }

    /// <summary>Whitelisted: <c>fullName</c>, <c>email</c>, <c>createdAt</c> (<c>API-004</c>).</summary>
    public string? Sort { get; init; }

    public string? Order { get; init; }
}
