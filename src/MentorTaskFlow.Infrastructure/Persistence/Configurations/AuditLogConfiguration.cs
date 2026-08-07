using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs", table =>
        {
            // Generated from AuditActions.OrganizationLevelActions, so the constraint and the code
            // cannot drift apart (TEN-048).
            var organizationLevel = string.Join(
                ",",
                AuditActions.OrganizationLevelActions.Order(StringComparer.Ordinal).Select(a => $"'{a}'"));

            table.HasCheckConstraint(
                "ck_audit_logs_branch_scope",
                $"branch_id IS NOT NULL OR action IN ({organizationLevel})");

            table.HasCheckConstraint(
                "ck_audit_logs_actor_shape",
                "(actor_type = 'System' AND actor_id IS NULL) OR (actor_type = 'User' AND actor_id IS NOT NULL)");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.ActorType).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(x => x.ActorRole).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.ActorAdminScope).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Action).HasMaxLength(AuditLog.ActionMaxLength).IsRequired();
        builder.Property(x => x.EntityType).HasMaxLength(AuditLog.EntityTypeMaxLength).IsRequired();
        builder.Property(x => x.HttpMethod).HasMaxLength(8);
        builder.Property(x => x.Path).HasMaxLength(AuditLog.PathMaxLength);
        builder.Property(x => x.IpAddress).HasMaxLength(45);
        builder.Property(x => x.UserAgent).HasMaxLength(AuditLog.UserAgentMaxLength);
        builder.Property(x => x.Result).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(x => x.FailureReason).HasMaxLength(AuditLog.FailureReasonMaxLength);
        builder.Property(x => x.CorrelationId).IsRequired();
        builder.Property(x => x.MetadataSchemaVersion).IsRequired().HasDefaultValue(1);

        // jsonb, not text or varchar (DEPLOY-005). No GIN index: querying by JSON content is out of
        // scope for Release 1.0, and the index would cost writes on every audited action.
        builder.Property(x => x.Metadata).HasColumnType("jsonb");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .HasConstraintName("fk_audit_logs_organization")
            .OnDelete(DeleteBehavior.Restrict);

        // fk_audit_logs_branch_scope: a record can never name a branch of a foreign organization.
        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(x => new { x.BranchId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId })
            .HasConstraintName("fk_audit_logs_branch_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // Tenant-leading, as TEN-029 requires of every list query.
        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.OccurredAt })
            .HasDatabaseName("ix_audit_logs_organization_branch_occurred_at")
            .IsDescending(false, false, true);

        builder.HasIndex(x => new { x.OrganizationId, x.EntityType, x.EntityId })
            .HasDatabaseName("ix_audit_logs_organization_entity");

        builder.HasIndex(x => new { x.OrganizationId, x.ActorId, x.OccurredAt })
            .HasDatabaseName("ix_audit_logs_organization_actor_occurred_at")
            .IsDescending(false, false, true);

        // Append-only: no concurrency token, and UPDATE/DELETE are revoked from the application role
        // in the migration (AUD-001, TZ 12.6).
    }
}
