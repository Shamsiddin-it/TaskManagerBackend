using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("branches", table =>
        {
            table.HasCheckConstraint(
                "ck_branches_code_format",
                $"code ~ '{BranchCodeFormat.Pattern}' AND char_length(code) BETWEEN {BranchCodeFormat.MinLength} AND {BranchCodeFormat.MaxLength}");

            table.HasCheckConstraint(
                "ck_branches_name_length",
                $"char_length(name) BETWEEN {Branch.NameMinLength} AND {Branch.NameMaxLength}");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(Branch.NameMaxLength).IsRequired();
        builder.Property(x => x.NormalizedName).HasMaxLength(Branch.NameMaxLength).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(BranchCodeFormat.MaxLength).IsRequired();
        builder.Property(x => x.Address).HasMaxLength(Branch.AddressMaxLength);
        builder.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.IsHeadOffice).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();

        // fk_branches_organization — a branch outside an organization becomes impossible.
        // ON DELETE RESTRICT everywhere; the schema has no cascades at all (DEPLOY-007, TEN-021).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .HasConstraintName("fk_branches_organization")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.OrganizationId, x.Code })
            .IsUnique()
            .HasDatabaseName("ux_branches_organization_code");

        builder.HasIndex(x => new { x.OrganizationId, x.NormalizedName })
            .IsUnique()
            .HasDatabaseName("ux_branches_organization_normalized_name");

        // Exactly one head office per organization. A partial unique index, not a service check:
        // two concurrent transactions would both pass a service check and leave the organization
        // with two head offices (BRN-021, TEST-TEN-032).
        builder.HasIndex(x => x.OrganizationId)
            .IsUnique()
            .HasFilter("is_head_office = true")
            .HasDatabaseName("ux_branches_single_head_office");

        // Target of the composite FKs from categories and users. Exists solely for that purpose —
        // it is never used for lookups and does not replace the ordinary indexes (TEN-020).
        builder.HasIndex(x => new { x.Id, x.OrganizationId })
            .IsUnique()
            .HasDatabaseName("ux_branches_id_organization");

        builder.HasIndex(x => new { x.OrganizationId, x.IsActive })
            .HasDatabaseName("ix_branches_organization_is_active");

        builder.ApplyConcurrencyToken();
    }
}
