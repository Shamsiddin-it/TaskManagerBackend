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
