namespace MentorTaskFlow.Application.Common.Tenancy;

/// <summary>
/// Claim names of the access token (<c>AUTH-003</c>).
/// </summary>
/// <remarks>
/// A claim that does not apply to a role is <b>not serialized</b> — a null is never written into the
/// token (<c>AUTH-031</c>). Authorization middleware rejects any token whose claim set matches no row
/// of the <c>AUTH-031</c> table, for example <c>role=Lead</c> without <c>branch_id</c>, and does so
/// before any business logic and without revealing which claim was wrong (<c>AUTH-032</c>).
/// </remarks>
public static class MtfClaimTypes
{
    public const string Subject = "sub";
    public const string Role = "role";
    public const string AdminScope = "admin_scope";
    public const string OrganizationId = "org_id";
    public const string BranchId = "branch_id";
    public const string CategoryId = "category_id";
    public const string TokenVersion = "tv";
}
