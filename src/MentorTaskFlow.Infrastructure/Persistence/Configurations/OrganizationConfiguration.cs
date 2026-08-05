using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations", table =>
        {
            table.HasCheckConstraint(
                "ck_organizations_slug_format",
                $"slug ~ '{SlugFormat.Pattern}' AND char_length(slug) BETWEEN {SlugFormat.MinLength} AND {SlugFormat.MaxLength}");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(Organization.NameMaxLength).IsRequired();
        builder.Property(x => x.NormalizedName).HasMaxLength(Organization.NameMaxLength).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(Organization.SlugMaxLength).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();

        builder.HasIndex(x => x.Slug)
            .IsUnique()
            .HasDatabaseName("ux_organizations_slug");

        // One installation must not hold two organizations with the same name: it invites
        // provisioning mistakes and mis-addressed operational reports (TZ 12.1a).
        builder.HasIndex(x => x.NormalizedName)
            .IsUnique()
            .HasDatabaseName("ux_organizations_normalized_name");

        builder.ApplyConcurrencyToken();
    }
}
