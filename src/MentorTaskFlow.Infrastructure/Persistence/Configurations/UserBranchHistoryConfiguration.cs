using MentorTaskFlow.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class UserBranchHistoryConfiguration : IEntityTypeConfiguration<UserBranchHistory>
{
    public void Configure(EntityTypeBuilder<UserBranchHistory> builder)
    {
        builder.ToTable("user_branch_history", table =>
        {
            table.HasCheckConstraint(
                "ck_user_branch_history_reason",
                $"char_length(reason) BETWEEN {UserBranchHistory.ReasonMinLength} AND {UserBranchHistory.ReasonMaxLength}");

            // IS DISTINCT FROM, not <>: with a NULL on either side, <> yields NULL and the CHECK
            // would pass. A transfer «to the same branch» must never be recorded (BRN-026).
            table.HasCheckConstraint(
                "ck_user_branch_history_change",
                "old_branch_id IS DISTINCT FROM new_branch_id");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.ChangedById).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(UserBranchHistory.ReasonMaxLength).IsRequired();
        builder.Property(x => x.CorrelationId).IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_user_branch_history_user")
            .OnDelete(DeleteBehavior.Restrict);

        // fk_user_branch_history_scope, old and new sides. Both are plain FKs to branches with the
        // organization carried alongside, so a transfer between branches of different organizations
        // cannot be recorded — matching ORG-023, which forbids the transfer itself.
        builder.HasOne<Domain.Tenancy.Branch>()
            .WithMany()
            .HasForeignKey(x => new { x.OldBranchId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId })
            .HasConstraintName("fk_user_branch_history_old_branch_scope")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Domain.Tenancy.Branch>()
            .WithMany()
            .HasForeignKey(x => new { x.NewBranchId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId })
            .HasConstraintName("fk_user_branch_history_new_branch_scope")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.OrganizationId, x.UserId, x.ChangedAt })
            .HasDatabaseName("ix_user_branch_history_organization_user_changed_at")
            .IsDescending(false, false, true);

        // Append-only: no concurrency token.
    }
}
