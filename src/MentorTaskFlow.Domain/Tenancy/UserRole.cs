namespace MentorTaskFlow.Domain.Tenancy;

/// <summary>
/// The complete set of roles. Exactly three, and version 2.2 adds none (<c>TEN-011</c>).
/// </summary>
/// <remarks>
/// <c>SuperAdmin</c>, <c>HeadOfficeAdmin</c>, <c>BranchAdminRole</c> and <c>OrganizationAdminRole</c>
/// do not exist. Administrative reach is expressed by the pair
/// (<see cref="Admin"/>, <see cref="AdminScope"/>) instead, because every additional role would
/// duplicate the whole permission matrix, every policy and every test without granting a single new
/// permission (ADR-001 §2.7).
/// </remarks>
public enum UserRole
{
    Admin = 0,
    Lead = 1,
    Mentor = 2,
}
