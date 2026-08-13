using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class AssignmentConfiguration : IEntityTypeConfiguration<Assignment>
{
    public void Configure(EntityTypeBuilder<Assignment> builder)
    {
        builder.ToTable("assignments", table =>
        {
            table.HasCheckConstraint(
                "ck_assignments_status_allowed",
                "status IN ('Draft','Suggested','Assigned','Submitted','InReview','NeedsRework','Overdue','Approved','Cancelled')");

            table.HasCheckConstraint(
                "ck_assignments_cancel_fields",
                $"""
                 (status <> 'Cancelled')
                 OR (cancelled_at IS NOT NULL AND cancelled_by_id IS NOT NULL
                     AND char_length(cancel_reason) BETWEEN {Assignment.CancelReasonMinLength} AND {Assignment.CancelReasonMaxLength})
                 """);

            table.HasCheckConstraint(
                "ck_assignments_approved_fields",
                "(status <> 'Approved') OR (approved_at IS NOT NULL)");

            // SCH-008: the auto-generation fields exist together or not at all. Version 2.0 declared a
            // uniqueness rule over a PlannedDate that was not physically present on the row, which
            // made the constraint unimplementable.
            table.HasCheckConstraint(
                "ck_assignments_auto_fields",
                """
                (source = 'Auto' AND generated_for_date IS NOT NULL AND auto_generation_key IS NOT NULL)
                OR (source = 'Manual' AND generated_for_date IS NULL AND auto_generation_key IS NULL)
                """);

            // The working deadline may move forward on rework but never behind the original.
            table.HasCheckConstraint(
                "ck_assignments_due_order",
                "current_due_at >= initial_due_at");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.CategoryId).IsRequired();
        builder.Property(x => x.AssignedToId).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(Assignment.TitleMaxLength).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(Assignment.DescriptionMaxLength);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(x => x.InitialDueAt).IsRequired();
        builder.Property(x => x.CurrentDueAt).IsRequired();
        builder.Property(x => x.AutoGenerationKey).HasMaxLength(120);
        builder.Property(x => x.CancelReason).HasMaxLength(Assignment.CancelReasonMaxLength);
        builder.Property(x => x.LastEventSequence).IsRequired().HasDefaultValue(0);

        // fk_assignments_category_scope
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => new { x.CategoryId, x.OrganizationId, x.BranchId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId, x.BranchId })
            .HasConstraintName("fk_assignments_category_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // fk_assignments_template_scope: an assignment cannot be built from a template of another
        // branch or another category.
        builder.HasOne<TopicAssignment>()
            .WithMany()
            .HasForeignKey(x => new { x.TopicAssignmentId, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasConstraintName("fk_assignments_template_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // fk_assignments_assignee_scope and fk_assignments_assigner_scope are declared as raw SQL in
        // the migration rather than here.
        //
        // EF Core's HasPrincipalKey demands a real UNIQUE CONSTRAINT, and PostgreSQL only allows one
        // over NOT NULL columns — so mapping these through the model made EF try to alter
        // users.branch_id and users.category_id to NOT NULL. That would destroy USER-023: an
        // Organization Admin has both null by definition. PostgreSQL is happy to point a foreign key
        // at a unique *index*, and ux_users_id_scope is exactly that, so the constraints are created
        // directly and the model simply does not know about them (TEN-024, TEST-TEN-014).

        // SCH-023: the tenant scope is part of the key even though TopicAssignmentId alone would
        // determine it. That keeps the index tenant-leading, keeps the key readable during an
        // incident, and survives any future change to identifier generation.
        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.AutoGenerationKey })
            .IsUnique()
            .HasFilter("source = 'Auto'")
            .HasDatabaseName("ux_assignments_auto_generation_key_scoped");

        // Target of the composite FKs from submissions, reviews and task_events.
        builder.HasIndex(x => new { x.Id, x.OrganizationId, x.BranchId, x.CategoryId })
            .IsUnique()
            .HasDatabaseName("ux_assignments_id_scope");

        // Mentor and Lead task lists, tenant-leading as TEN-029 requires.
        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.CategoryId, x.AssignedToId, x.Status, x.CurrentDueAt })
            .HasDatabaseName("ix_assignments_scope_assignee_status_due");

        // The overdue job scans by status and deadline across every tenant, so this one is
        // deliberately not tenant-leading — it is the single query that must not be.
        builder.HasIndex(x => new { x.Status, x.CurrentDueAt })
            .HasFilter("status IN ('Assigned','NeedsRework')")
            .HasDatabaseName("ix_assignments_overdue_scan");

        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.CategoryId, x.Status })
            .HasFilter("status = 'Suggested'")
            .HasDatabaseName("ix_assignments_suggestion_queue");

        builder.HasIndex(x => x.ApprovedAt)
            .HasFilter("approved_at IS NOT NULL")
            .HasDatabaseName("ix_assignments_approved_at");

        builder.ApplyConcurrencyToken();
    }
}
