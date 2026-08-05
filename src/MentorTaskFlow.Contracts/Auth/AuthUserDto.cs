namespace MentorTaskFlow.Contracts.Auth;

/// <summary>Minimal organization view, available to any authenticated user (<c>ORG-003</c>).</summary>
/// <remarks>
/// Only <c>id</c> and <c>name</c>. <c>Slug</c>, <c>IsActive</c> and the service fields are withheld
/// from Lead, Mentor and Branch Admin: no scenario of theirs needs them, and shipping them would
/// widen the disclosure surface for nothing.
/// </remarks>
public sealed record OrganizationSummaryDto(Guid Id, string Name);

/// <summary>Minimal branch view embedded in auth responses and list rows (<c>AUTH-038</c>).</summary>
/// <remarks>
/// Excludes <c>address</c> and <c>timeZoneId</c> on purpose: the time zone relevant to a Lead or
/// Mentor comes from <c>CategorySettings</c>, and the address plays no part in any of their
/// scenarios (<c>BRN-009</c>).
/// </remarks>
public sealed record BranchSummaryDto(Guid Id, string Name, string Code, bool IsHeadOffice);

/// <summary>
/// The single authoritative profile shape, returned identically by <c>POST /auth/login</c> and
/// <c>GET /auth/me</c> (<c>AUTH-037</c>).
/// </summary>
/// <remarks>
/// <para>
/// The frontend is not a trusted source of the profile: whatever it decodes from the JWT is a UX hint
/// until this response arrives, and the backend never accepts scope handed back by a client
/// (<c>AUTH-036</c>, <c>SEC-003</c>).
/// </para>
/// <para>
/// Field population is fully determined by the user type (<c>AUTH-038</c>): an Organization Admin has
/// <c>branch = null</c> and <c>categoryId = null</c>; a Branch Admin has a branch and no category;
/// Lead and Mentor have both. <c>organization</c> is always present.
/// </para>
/// </remarks>
public sealed record AuthUserDto(
    Guid Id,
    string FullName,
    string Email,
    string Role,
    string? AdminScope,
    OrganizationSummaryDto Organization,
    BranchSummaryDto? Branch,
    Guid? CategoryId);

/// <summary>Response of <c>POST /auth/login</c> — the profile plus the access token (<c>AUTH-039</c>).</summary>
/// <remarks>
/// The refresh token is <b>not</b> here: it travels only in the <c>mtf_rt</c> cookie, which is
/// HttpOnly so that script running on the page cannot read it (<c>AUTH-010</c>).
/// </remarks>
public sealed record LoginResponse(
    AuthUserDto User,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    bool? TelegramBound);
