using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class UserCategoryHistoryConfiguration : IEntityTypeConfiguration<UserCategoryHistory>
{
    public void Configure(EntityTypeBuilder<UserCategoryHistory> builder)
    {
        builder.ToTable("user_category_history", table =>
        {
            table.HasCheckConstraint(
                "ck_user_category_history_reason",
                $"char_length(reason) BETWEEN {UserCategoryHistory.ReasonMinLength} AND {UserCategoryHistory.ReasonMaxLength}");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.PreviousRole).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.NewRole).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.ChangedById).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(UserCategoryHistory.ReasonMaxLength).IsRequired();
        builder.Property(x => x.CorrelationId).IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_user_category_history_user")
            .OnDelete(DeleteBehavior.Restrict);

        // fk_user_category_history_scope: a transfer row can never point at a branch of a foreign
        // organization.
        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(x => new { x.BranchId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId })
            .HasConstraintName("fk_user_category_history_scope")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.OrganizationId, x.UserId, x.ChangedAt })
            .HasDatabaseName("ix_user_category_history_organization_user_changed_at")
            .IsDescending(false, false, true);

        // No concurrency token: the table is append-only and never updated (TZ 11.6).
    }
}
