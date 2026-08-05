namespace MentorTaskFlow.Infrastructure.Options;

/// <summary>
/// Provisioning input for the first organization (TZ 32.6, <c>DEPLOY-023</c>).
/// </summary>
/// <remarks>
/// There is deliberately <b>no password variable</b>. The first administrator receives a
/// set-password link and nothing else; default credentials such as <c>admin/admin</c> do not exist
/// anywhere in the system (<c>DEPLOY-025</c>, <c>AUTH-019</c>).
/// </remarks>
public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    /// <summary><c>BOOTSTRAP_ORGANIZATION_NAME</c>, for example «SoftClub Academy».</summary>
    public string? OrganizationName { get; init; }

    /// <summary><c>BOOTSTRAP_ORGANIZATION_SLUG</c>, globally unique.</summary>
    public string? OrganizationSlug { get; init; }

    /// <summary><c>BOOTSTRAP_HEAD_OFFICE_NAME</c>.</summary>
    public string? HeadOfficeName { get; init; }

    /// <summary><c>BOOTSTRAP_HEAD_OFFICE_CODE</c>, for example «HQ».</summary>
    public string? HeadOfficeCode { get; init; }

    /// <summary><c>BOOTSTRAP_HEAD_OFFICE_TIMEZONE</c>, IANA identifier.</summary>
    public string? HeadOfficeTimeZone { get; init; }

    /// <summary><c>BOOTSTRAP_ADMIN_EMAIL</c> — the first Organization Admin.</summary>
    public string? AdminEmail { get; init; }

    /// <summary>True when every required value is present, so the step can run.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(OrganizationName)
        && !string.IsNullOrWhiteSpace(OrganizationSlug)
        && !string.IsNullOrWhiteSpace(HeadOfficeName)
        && !string.IsNullOrWhiteSpace(HeadOfficeCode)
        && !string.IsNullOrWhiteSpace(HeadOfficeTimeZone)
        && !string.IsNullOrWhiteSpace(AdminEmail);
}
