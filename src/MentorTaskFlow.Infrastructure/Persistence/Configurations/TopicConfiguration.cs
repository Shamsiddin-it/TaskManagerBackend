using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class TopicConfiguration : IEntityTypeConfiguration<Topic>
{
    public void Configure(EntityTypeBuilder<Topic> builder)
    {
        builder.ToTable("topics", table =>
        {
            table.HasCheckConstraint("ck_topics_day_number", "day_number > 0");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.CategoryId).IsRequired();
        builder.Property(x => x.DayNumber).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(Topic.TitleMaxLength).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(Topic.DescriptionMaxLength);
        builder.Property(x => x.IsActive).IsRequired();

        // fk_topics_category_scope: a topic in a category of another branch becomes impossible.
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => new { x.CategoryId, x.OrganizationId, x.BranchId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId, x.BranchId })
            .HasConstraintName("fk_topics_category_scope")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.CategoryId, x.DayNumber })
            .IsUnique()
            .HasDatabaseName("ux_topics_category_day");

        // Partial: only among active topics. Two archived topics may legitimately share a date, and
        // the constraint exists to keep the scheduler's selection unambiguous, not to police history
        // (TOPIC-010).
        builder.HasIndex(x => new { x.CategoryId, x.PlannedDate })
            .IsUnique()
            .HasFilter("planned_date IS NOT NULL AND is_active = true")
            .HasDatabaseName("ux_topics_category_planned_date");

        // Target of the composite FK from topic_assignments.
        builder.HasIndex(x => new { x.Id, x.OrganizationId, x.BranchId, x.CategoryId })
            .IsUnique()
            .HasDatabaseName("ux_topics_id_scope");

        // Tenant-leading, as TEN-029 requires of the scheduler's selection query.
        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.CategoryId, x.IsActive, x.PlannedDate })
            .HasDatabaseName("ix_topics_organization_branch_category_active_planned_date");

        builder.ApplyConcurrencyToken();
    }
}
