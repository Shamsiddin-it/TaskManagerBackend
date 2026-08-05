using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Categories;

/// <summary>
/// A study track (C#, Python, Go…) <b>inside one branch</b> (TZ 10.2).
/// </summary>
/// <remarks>
/// Categories with the same name in different branches are different entities with no shared users,
/// tasks, files, notifications or analytics. Uniqueness is therefore
/// <c>UNIQUE (branch_id, normalized_name)</c>, not global: version 2.1's global constraint is
/// cancelled (<c>CAT-021</c>).
/// </remarks>
public sealed class Category : AuditableEntity
{
    /// <summary>
    /// 2, not the «3–50» stated in TZ 10.2 — a deliberate, documented deviation.
    /// </summary>
    /// <remarks>
    /// The TZ contradicts itself: section 2 names the study tracks as «C#, Python, Go, Frontend,
    /// Design», and the isolation fixture of section 31.9 builds its whole scenario on a category
    /// called <c>C#</c>. Both <c>C#</c> and <c>Go</c> are two characters, so a minimum of 3 would make
    /// the system unable to model the example its own requirements are written around. The upper
    /// bound of 50 and the <c>varchar(50)</c> column are kept exactly as specified.
    /// </remarks>
    public const int NameMinLength = 2;

    public const int NameMaxLength = 50;
    public const int DescriptionMaxLength = 500;

    /// <summary>Immutable — see <c>CAT-022</c>.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// Immutable. Moving a category between branches is unsupported: it would distort the historical
    /// data of both. The equivalent effect is achieved by creating a new category in the target
    /// branch and transferring users (<c>CAT-022</c>).
    /// </summary>
    public Guid BranchId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>Server-computed; participates in <c>ux_categories_branch_normalized_name</c>.</summary>
    public string NormalizedName { get; private set; } = null!;

    public string? Description { get; private set; }

    public bool IsActive { get; private set; }

    private Category()
    {
    }

    public static Category Create(
        Guid organizationId,
        Guid branchId,
        string name,
        string? description,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || branchId == Guid.Empty)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "OrganizationId и BranchId обязательны и определяются сервером.");
        }

        var trimmedName = (name ?? string.Empty).Trim();
        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (trimmedName.Length is < NameMinLength or > NameMaxLength)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Название категории должно содержать от {NameMinLength} до {NameMaxLength} символов.");
        }

        if (trimmedDescription is { Length: > DescriptionMaxLength })
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Описание категории не должно превышать {DescriptionMaxLength} символов.");
        }

        return new Category
        {
            OrganizationId = organizationId,
            BranchId = branchId,
            Name = trimmedName,
            NormalizedName = Normalization.ToNormalized(trimmedName),
            Description = trimmedDescription,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Update(string name, string? description, DateTimeOffset now)
    {
        var updated = Create(OrganizationId, BranchId, name, description, now);

        Name = updated.Name;
        NormalizedName = updated.NormalizedName;
        Description = updated.Description;
        UpdatedAt = now;
    }

    /// <summary>
    /// Deactivation blocks writes in the category's contour (403 <c>CATEGORY_INACTIVE</c>) but does
    /// not change the status of existing assignments and does not cancel them (<c>CAT-010</c>,
    /// <c>CAT-011</c>). Historical reads stay available (<c>CAT-012</c>).
    /// </summary>
    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        IsActive = true;
        UpdatedAt = now;
    }
}
