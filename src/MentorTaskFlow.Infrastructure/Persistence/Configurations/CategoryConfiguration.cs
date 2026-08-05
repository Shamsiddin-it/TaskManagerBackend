using MentorTaskFlow.Domain.Categories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(Category.NameMaxLength).IsRequired();
        builder.Property(x => x.NormalizedName).HasMaxLength(Category.NameMaxLength).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(Category.DescriptionMaxLength);
        builder.Property(x => x.IsActive).IsRequired();

        // fk_categories_branch_scope: (branch_id, organization_id) → branches(id, organization_id).
        // A category in a branch of a foreign organization becomes impossible (CAT-020).
        builder.HasOne<Domain.Tenancy.Branch>()
            .WithMany()
            .HasForeignKey(x => new { x.BranchId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId })
            .HasConstraintName("fk_categories_branch_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // Name uniqueness is per branch, not global: `C#` may exist in the head office and in the
        // Khujand branch simultaneously as two different entities (CAT-021, TEST-TEN-031).
        builder.HasIndex(x => new { x.BranchId, x.NormalizedName })
            .IsUnique()
            .HasDatabaseName("ux_categories_branch_normalized_name");

        // Target of the composite FKs from category_settings, topics, assignments and users.
        builder.HasIndex(x => new { x.Id, x.OrganizationId, x.BranchId })
            .IsUnique()
            .HasDatabaseName("ux_categories_id_scope");

        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.IsActive })
            .HasDatabaseName("ix_categories_organization_branch_is_active");

        builder.ApplyConcurrencyToken();
    }
}
