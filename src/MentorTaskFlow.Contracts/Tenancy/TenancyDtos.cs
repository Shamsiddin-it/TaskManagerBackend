namespace MentorTaskFlow.Contracts.Tenancy;

/// <summary>Minimal organization view, available to any authenticated user (<c>ORG-003</c>).</summary>
/// <remarks>
/// Only <c>id</c> and <c>name</c>. <c>Slug</c>, <c>IsActive</c> and the service fields are withheld
/// from Lead, Mentor and Branch Admin: no scenario of theirs needs them, and shipping them would
/// widen the disclosure surface for nothing.
/// </remarks>
public sealed record OrganizationSummaryDto(Guid Id, string Name);

/// <summary>Full organization profile — Organization Admin only (<c>API-030</c>).</summary>
public sealed record OrganizationDto(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    DateTimeOffset CreatedAt,
    string ConcurrencyToken);

/// <summary>
/// <c>PUT /organization</c>.
/// </summary>
/// <remarks>
/// <c>name</c> is the only mutable field. <c>Slug</c> is immutable — it may appear in invitation links
/// and operational procedures — and <c>IsActive</c> is driven by provisioning alone, so neither
/// appears in the schema at all rather than being accepted and ignored (<c>ORG-004</c>).
/// </remarks>
public sealed record UpdateOrganizationRequest(string Name, string ConcurrencyToken);

/// <summary>Minimal branch view embedded in auth responses and list rows (<c>AUTH-038</c>).</summary>
/// <remarks>
/// Excludes <c>address</c> and <c>timeZoneId</c> deliberately: the time zone relevant to a Lead or
/// Mentor comes from <c>CategorySettings</c>, and the address plays no part in any of their scenarios
/// (<c>BRN-009</c>).
/// </remarks>
public sealed record BranchSummaryDto(Guid Id, string Name, string Code, bool IsHeadOffice);

/// <summary>Full branch profile, for the two administrative contours (<c>API-030</c>).</summary>
public sealed record BranchDto(
    Guid Id,
    Guid OrganizationId,
    string Name,
    string Code,
    string? Address,
    string TimeZoneId,
    bool IsHeadOffice,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken);

/// <summary>
/// <c>POST /branches</c>.
/// </summary>
/// <remarks>
/// <c>organizationId</c> is absent because it always comes from the principal (<c>SEC-003</c>), and
/// <c>isHeadOffice</c> is absent because the flag is only ever set by <c>make-head-office</c>. Allowing
/// it at creation would open a path to two head offices before the unique index fires, and make the
/// failure harder to diagnose (<c>API-031</c>, <c>BRN-042</c>).
/// </remarks>
public sealed record CreateBranchRequest(string Name, string Code, string? Address, string TimeZoneId);

/// <summary><c>PUT /branches/{id}</c> — Organization Admin only (<c>BRN-002</c>).</summary>
public sealed record UpdateBranchRequest(
    string Name,
    string Code,
    string? Address,
    string TimeZoneId,
    string ConcurrencyToken);

/// <summary>
/// <c>POST /branches/{id}/deactivate</c>.
/// </summary>
/// <remarks>
/// <c>confirmActiveUsers</c> must be true when the branch still holds active users; otherwise the
/// operation answers 409 <c>BRANCH_HAS_ACTIVE_USERS</c>. Users are <b>not</b> deactivated with the
/// branch (<c>BRN-030</c>).
/// </remarks>
public sealed record DeactivateBranchRequest(string ConcurrencyToken, bool ConfirmActiveUsers = false);

/// <summary>Body of the remaining branch actions (<c>API-016</c>).</summary>
public sealed record BranchActionRequest(string ConcurrencyToken);

/// <summary>Filters for <c>GET /branches</c> (<c>BRN-006</c>).</summary>
public sealed record BranchListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    /// <summary>Null returns both active and deactivated branches.</summary>
    public bool? IsActive { get; init; }

    /// <summary>Whitelisted: <c>name</c>, <c>code</c>, <c>createdAt</c> (<c>API-004</c>).</summary>
    public string? Sort { get; init; }

    public string? Order { get; init; }
}
