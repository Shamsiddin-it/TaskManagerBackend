using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class NotificationOutboxConfiguration : IEntityTypeConfiguration<NotificationOutbox>
{
    public void Configure(EntityTypeBuilder<NotificationOutbox> builder)
    {
        builder.ToTable("notification_outbox", table =>
        {
            // Generated from NotificationEventTypes.OrganizationLevelEvents (TEN-042).
            var organizationLevel = string.Join(
                ",",
                NotificationEventTypes.OrganizationLevelEvents.Order(StringComparer.Ordinal).Select(e => $"'{e}'"));

            table.HasCheckConstraint(
                "ck_notification_outbox_branch_scope",
                $"branch_id IS NOT NULL OR event_type IN ({organizationLevel})");

            table.HasCheckConstraint(
                "ck_notification_outbox_attempts",
                $"attempts BETWEEN 0 AND {NotificationOutbox.MaxAttempts}");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.Channel).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(NotificationOutbox.EventTypeMaxLength).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.PayloadSchemaVersion).IsRequired().HasDefaultValue(1);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(12).IsRequired();
        builder.Property(x => x.Attempts).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.NextAttemptAt).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(NotificationOutbox.LastErrorMaxLength);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(NotificationOutbox.DeduplicationKeyMaxLength).IsRequired();
        builder.Property(x => x.ProviderMessageId).HasMaxLength(128);
        builder.Property(x => x.IsSystemAlert).IsRequired().HasDefaultValue(false);
        builder.Property(x => x.LockedBy).HasMaxLength(64);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_notification_outbox_user")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .HasConstraintName("fk_notification_outbox_organization")
            .OnDelete(DeleteBehavior.Restrict);

        // fk_outbox_branch_scope: a notification can never be addressed to a branch of a foreign
        // organization.
        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(x => new { x.BranchId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId })
            .HasConstraintName("fk_outbox_branch_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // The final guard against duplicates: the writer inserts with ON CONFLICT DO NOTHING, so a
        // repeated job run silently skips instead of sending twice (NTF-015).
        builder.HasIndex(x => x.DeduplicationKey)
            .IsUnique()
            .HasDatabaseName("ux_notification_outbox_dedup");

        // Worker selection and recovery of stuck rows (TZ 12.3). Partial, because the worker only ever
        // looks at one status at a time and the table is dominated by Sent rows awaiting retention.
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt })
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ix_notification_outbox_pending");

        builder.HasIndex(x => new { x.Status, x.LockedAt })
            .HasFilter("status = 'Processing'")
            .HasDatabaseName("ix_notification_outbox_processing");

        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.Status, x.CreatedAt })
            .HasDatabaseName("ix_notification_outbox_organization_branch_status_created_at")
            .IsDescending(false, false, false, true);
    }
}
