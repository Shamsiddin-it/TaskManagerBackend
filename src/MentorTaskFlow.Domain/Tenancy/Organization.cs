using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Tenancy;

/// <summary>
/// The top tenant boundary — one IT academy or company (TZ 10.17).
/// </summary>
/// <remarks>
/// Data of different organizations never intersects under any circumstance. No user of Organization A
/// learns that Organization B exists: not from lists, counters, search, error messages or any metric
/// visible to them (<c>ORG-022</c>).
/// </remarks>
public sealed class Organization : AuditableEntity
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 200;
    public const int SlugMinLength = 2;
    public const int SlugMaxLength = 80;

    public string Name { get; private set; } = null!;

    /// <summary>Server-computed; a client may never supply it (<c>ORG-020</c>).</summary>
    public string NormalizedName { get; private set; } = null!;

    /// <summary>
    /// Globally unique, immutable after creation: it may appear in invitation links and in
    /// operational procedures, so changing it would break already-issued references (<c>ORG-020</c>).
    /// </summary>
    public string Slug { get; private set; } = null!;

    public bool IsActive { get; private set; }

    private Organization()
    {
    }

    /// <summary>
    /// Created only by the provisioning process (<c>ORG-001</c>). There is no public or administrative
    /// API for it — the endpoint is absent from the contract, not merely forbidden by policy
    /// (<c>ORG-002</c>).
    /// </summary>
    public static Organization Provision(string name, string slug, DateTimeOffset now)
    {
        var trimmedName = (name ?? string.Empty).Trim();
        var trimmedSlug = (slug ?? string.Empty).Trim();

        if (trimmedName.Length is < NameMinLength or > NameMaxLength)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Название организации должно содержать от {NameMinLength} до {NameMaxLength} символов.");
        }

        if (!SlugFormat.IsValid(trimmedSlug))
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "Slug организации должен соответствовать шаблону ^[a-z0-9]+(-[a-z0-9]+)*$ и содержать от 2 до 80 символов.");
        }

        return new Organization
        {
            Name = trimmedName,
            NormalizedName = Normalization.ToNormalized(trimmedName),
            Slug = trimmedSlug,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// The only mutable field is <see cref="Name"/> (<c>ORG-004</c>). <see cref="Slug"/> is immutable
    /// and <see cref="IsActive"/> is driven by provisioning alone (<c>ORG-007</c>), so neither is
    /// reachable from this method.
    /// </summary>
    public void Rename(string name, DateTimeOffset now)
    {
        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length is < NameMinLength or > NameMaxLength)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Название организации должно содержать от {NameMinLength} до {NameMaxLength} символов.");
        }

        Name = trimmed;
        NormalizedName = Normalization.ToNormalized(trimmed);
        UpdatedAt = now;
    }

    /// <summary>
    /// Deactivation is an operational action performed by provisioning, not a product feature. While
    /// inactive, every operation by this organization's users — reads included — returns 403
    /// <c>ORGANIZATION_INACTIVE</c> (<c>ORG-007</c>).
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
