using MentorTaskFlow.Domain.Assignments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class TaskEventConfiguration : IEntityTypeConfiguration<TaskEvent>
{
    public void Configure(EntityTypeBuilder<TaskEvent> builder)
    {
        builder.ToTable("task_events", table =>
        {
            // A system event has no actor. The two that qualify are MarkedOverdue and
            // SuggestedCreated — both produced by background jobs (10.9).
            table.HasCheckConstraint(
                "ck_task_events_system_actor",
                "(event_type NOT IN ('MarkedOverdue','SuggestedCreated')) OR actor_id IS NULL");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.AssignmentId).IsRequired();
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.CategoryId).IsRequired();
        builder.Property(x => x.SequenceNumber).IsRequired();
        builder.Property(x => x.EventType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.PreviousStatus).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.NewStatus).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.OccurredAt).IsRequired();
        builder.Property(x => x.CorrelationId).IsRequired();
        builder.Property(x => x.Metadata).HasColumnType("jsonb");
        builder.Property(x => x.MetadataSchemaVersion).IsRequired().HasDefaultValue(1);

        // fk_task_events_assignment_scope: an event whose scope differs from its assignment becomes
        // physically impossible (EVT-007).
        builder.HasOne<Assignment>()
            .WithMany()
            .HasForeignKey(x => new { x.AssignmentId, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasConstraintName("fk_task_events_assignment_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // The final guard on ordering: if two writers ever allocated the same sequence number, the
        // transaction is rolled back rather than the history being silently corrupted (12.4).
        builder.HasIndex(x => new { x.AssignmentId, x.SequenceNumber })
            .IsUnique()
            .HasDatabaseName("ux_task_events_assignment_sequence");

        builder.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_task_events_correlation_id");

        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.OccurredAt })
            .HasDatabaseName("ix_task_events_organization_branch_occurred_at")
            .IsDescending(false, false, true);

        // Append-only: no concurrency token, and UPDATE/DELETE are revoked in the migration.
    }
}
