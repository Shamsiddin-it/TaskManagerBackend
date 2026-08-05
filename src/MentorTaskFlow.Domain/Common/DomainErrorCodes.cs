namespace MentorTaskFlow.Domain.Common;

/// <summary>
/// The subset of the error catalog that domain methods raise.
/// </summary>
/// <remarks>
/// Domain references no other project, so it cannot use <c>MentorTaskFlow.Contracts.ErrorCodes</c>
/// directly. Duplicating the literals here is the price of that isolation; the compensating check is
/// <c>DomainErrorCodeTests</c>, which asserts every value below exists in the real catalog with the
/// expected status. A code invented here and absent from Приложение C fails that test.
/// </remarks>
public static class DomainErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string CrossScopeReference = "CROSS_SCOPE_REFERENCE";
    public const string BranchAlreadyExists = "BRANCH_ALREADY_EXISTS";
    public const string HeadOfficeRequired = "HEAD_OFFICE_REQUIRED";
    public const string HeadOfficeDeactivationForbidden = "HEAD_OFFICE_DEACTIVATION_FORBIDDEN";
    public const string ResourceAlreadyExists = "RESOURCE_ALREADY_EXISTS";
    public const string CategoryInactive = "CATEGORY_INACTIVE";
    public const string BranchInactive = "BRANCH_INACTIVE";
    public const string OrganizationInactive = "ORGANIZATION_INACTIVE";
}
