using MentorTaskFlow.Domain.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class TopicAssignmentConfiguration : IEntityTypeConfiguration<TopicAssignment>
{
    public void Configure(EntityTypeBuilder<TopicAssignment> builder)
    {
        builder.ToTable("topic_assignments", table =>
        {
            table.HasCheckConstraint(
                "ck_topic_assignments_type_allowed",
                "type IN ('Presentation','ClassTask','HomeTask')");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.TopicId).IsRequired();
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.CategoryId).IsRequired();
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(TopicAssignment.TitleMaxLength).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(TopicAssignment.DescriptionMaxLength);
        builder.Property(x => x.IsRequired).IsRequired();
        builder.Property(x => x.IsActive).IsRequired();

        // fk_topic_assignments_topic_scope: a template bound to a topic of another branch or another
        // category becomes physically impossible (TPL-005).
        builder.HasOne<Topic>()
            .WithMany()
            .HasForeignKey(x => new { x.TopicId, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasConstraintName("fk_topic_assignments_topic_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // Target of the composite FK from assignments, which arrives with the assignment lifecycle.
        builder.HasIndex(x => new { x.Id, x.OrganizationId, x.BranchId, x.CategoryId })
            .IsUnique()
            .HasDatabaseName("ux_topic_assignments_id_scope");

        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.CategoryId, x.IsActive })
            .HasDatabaseName("ix_topic_assignments_organization_branch_category_active");

        builder.ApplyConcurrencyToken();
    }
}
