using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Tenancy;

/// <summary>
/// A branch — the operational isolation boundary inside an <see cref="Organization"/> (TZ 10.18).
/// </summary>
/// <remarks>
/// Users, categories and business data of one branch are unavailable to another. The head office is
/// an ordinary branch with <see cref="IsHeadOffice"/> set; it has no special technical contour
/// (<c>BRN-021</c>).
/// </remarks>
public sealed class Branch : AuditableEntity
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 200;
    public const int AddressMaxLength = 500;

    /// <summary>Immutable. Moving a branch between organizations is unsupported and has no endpoint (<c>BRN-020</c>).</summary>
    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>Server-computed; unique within the organization.</summary>
    public string NormalizedName { get; private set; } = null!;

    /// <summary>Unique within the organization; used in operational reports and exports.</summary>
    public string Code { get; private set; } = null!;

    public string? Address { get; private set; }

    /// <summary>IANA time zone id, validated against tzdata on every save (<c>BRN-010</c>).</summary>
    public string TimeZoneId { get; private set; } = null!;

    /// <summary>
    /// Exactly one branch per organization carries this flag. The guarantee comes from the partial
    /// unique index <c>ux_branches_single_head_office</c>, not from service code (<c>BRN-021</c>);
    /// an organization without a head office is an invalid state.
    /// </summary>
    public bool IsHeadOffice { get; private set; }

    public bool IsActive { get; private set; }

    private Branch()
    {
    }

    /// <summary>
    /// Creates a branch. <paramref name="isHeadOffice"/> is not a parameter by design: the flag is
    /// only ever set through the dedicated <c>make-head-office</c> operation. Allowing it at creation
    /// would open a path to «two head offices» before the unique index fires and would make the
    /// failure harder to diagnose (<c>API-031</c>, <c>BRN-042</c>).
    /// </summary>
    public static Branch Create(
        Guid organizationId,
        string name,
        string code,
        string? address,
        string timeZoneId,
        DateTimeOffset now)
    {
        var trimmedName = (name ?? string.Empty).Trim();
        var trimmedCode = (code ?? string.Empty).Trim();
        var trimmedAddress = string.IsNullOrWhiteSpace(address) ? null : address.Trim();

        if (organizationId == Guid.Empty)
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "OrganizationId обязателен.");
        }

        if (trimmedName.Length is < NameMinLength or > NameMaxLength)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Название филиала должно содержать от {NameMinLength} до {NameMaxLength} символов.");
        }

        if (!BranchCodeFormat.IsValid(trimmedCode))
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "Код филиала должен соответствовать шаблону ^[A-Z0-9][A-Z0-9-]*$ и содержать от 2 до 32 символов.");
        }

        if (trimmedAddress is { Length: > AddressMaxLength })
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Адрес филиала не должен превышать {AddressMaxLength} символов.");
        }

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new DomainException(DomainErrorCodes.ValidationFailed, "TimeZoneId обязателен.");
        }

        return new Branch
        {
            OrganizationId = organizationId,
            Name = trimmedName,
            NormalizedName = Normalization.ToNormalized(trimmedName),
            Code = trimmedCode,
            Address = trimmedAddress,
            TimeZoneId = timeZoneId,
            IsHeadOffice = false,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Creates the head office in the same transaction as its organization, so that «Organization
    /// without a Branch» is never observed by any reader (<c>BRN-028</c>).
    /// </summary>
    public static Branch CreateHeadOffice(
        Guid organizationId,
        string name,
        string code,
        string? address,
        string timeZoneId,
        DateTimeOffset now)
    {
        var branch = Create(organizationId, name, code, address, timeZoneId, now);
        branch.IsHeadOffice = true;
        return branch;
    }

    /// <summary>
    /// Editing is reserved for the Organization Admin (<c>BRN-002</c>). <see cref="IsHeadOffice"/> is
    /// deliberately unreachable here (<c>BRN-053</c>).
    /// </summary>
    public void Update(string name, string code, string? address, string timeZoneId, DateTimeOffset now)
    {
        var updated = Create(OrganizationId, name, code, address, timeZoneId, now);

        Name = updated.Name;
        NormalizedName = updated.NormalizedName;
        Code = updated.Code;
        Address = updated.Address;
        TimeZoneId = updated.TimeZoneId;
        UpdatedAt = now;
    }

    /// <summary>
    /// Transfers the head-office flag. The caller must clear the previous head office inside the same
    /// transaction and in that order — setting first would violate
    /// <c>ux_branches_single_head_office</c> mid-transaction (<c>BRN-035</c>).
    /// </summary>
    public void MarkAsHeadOffice(DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new DomainException(
                DomainErrorCodes.HeadOfficeRequired,
                "Главным офисом может быть назначен только активный филиал.");
        }

        IsHeadOffice = true;
        UpdatedAt = now;
    }

    public void ClearHeadOffice(DateTimeOffset now)
    {
        IsHeadOffice = false;
        UpdatedAt = now;
    }

    /// <summary>
    /// A deactivated branch keeps historical reads working and refuses every business mutation with
    /// 403 <c>BRANCH_INACTIVE</c> (<c>BRN-031</c>). The sole head office cannot be deactivated until
    /// the flag has been passed on, otherwise the organization would be left without one
    /// (<c>BRN-034</c>).
    /// </summary>
    public void Deactivate(DateTimeOffset now)
    {
        if (IsHeadOffice)
        {
            throw new DomainException(
                DomainErrorCodes.HeadOfficeDeactivationForbidden,
                "Нельзя деактивировать главный офис, пока признак не передан другому филиалу.");
        }

        IsActive = false;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        IsActive = true;
        UpdatedAt = now;
    }
}
