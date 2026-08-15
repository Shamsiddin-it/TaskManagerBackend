using MentorTaskFlow.Domain.Analytics;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class AiSummaryConfiguration : IEntityTypeConfiguration<AiSummary>
{
    public void Configure(EntityTypeBuilder<AiSummary> builder)
    {
        builder.ToTable("ai_summaries", table =>
        {
            // 10.16, verbatim: the shape of the row follows from the scope, and the database is where
            // that is guaranteed. A branch report with a category, or an organization aggregate with a
            // branch, would each be a report about something other than what it claims.
            table.HasCheckConstraint(
                "ck_ai_summaries_scope_shape",
                "(scope = 'Organization' AND branch_id IS NULL AND category_id IS NULL) "
                + "OR (scope = 'Branch' AND branch_id IS NOT NULL AND category_id IS NULL) "
                + "OR (scope IN ('Personal', 'Team') AND branch_id IS NOT NULL AND category_id IS NOT NULL)");

            table.HasCheckConstraint(
                "ck_ai_summaries_personal_subject",
                "scope <> 'Personal' OR subject_user_id IS NOT NULL");

            // Content is what «Completed» means. A completed row without it would be served from the
            // cache as an empty report and look like a successful generation (10.16).
            table.HasCheckConstraint(
                "ck_ai_summaries_completed_content",
                "status <> 'Completed' OR content IS NOT NULL");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.Scope).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(12).IsRequired();
        builder.Property(x => x.PeriodStart).IsRequired();
        builder.Property(x => x.PeriodEnd).IsRequired();
        builder.Property(x => x.CacheKey).HasMaxLength(AiSummary.CacheKeyMaxLength).IsRequired();
        builder.Property(x => x.MetricsHash).HasColumnType("char(64)").IsRequired();
        builder.Property(x => x.PromptVersion).HasMaxLength(AiSummary.PromptVersionMaxLength).IsRequired();
        builder.Property(x => x.ModelId).HasMaxLength(AiSummary.ModelIdMaxLength).IsRequired();
        builder.Property(x => x.Content).HasColumnType("text");
        builder.Property(x => x.FailureReason).HasMaxLength(AiSummary.FailureReasonMaxLength);
        builder.Property(x => x.RequestedById).IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .HasConstraintName("fk_ai_summaries_organization")
            .OnDelete(DeleteBehavior.Restrict);

        // fk_ai_summaries_branch_scope (composite FK 19 of 12.4): a report can never be attached to a
        // branch of another organization. The pair is what makes that impossible — a plain FK on
        // branch_id alone would accept any branch in the installation.
        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(x => new { x.BranchId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId })
            .HasConstraintName("fk_ai_summaries_branch_scope")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .HasConstraintName("fk_ai_summaries_category")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.SubjectUserId)
            .HasConstraintName("fk_ai_summaries_subject_user")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.RequestedById)
            .HasConstraintName("fk_ai_summaries_requested_by")
            .OnDelete(DeleteBehavior.Restrict);

        // The cache itself, and the last defence of TEN-076: two requests racing for the same report
        // both insert, one wins, and the loser re-reads the winner's row instead of calling the
        // provider a second time (Приложение N, race 15).
        builder.HasIndex(x => x.CacheKey)
            .IsUnique()
            .HasDatabaseName("ux_ai_summaries_cache_key");

        // Tenant-leading, per TEN-029: every list of reports starts from the organization.
        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.CategoryId, x.PeriodStart, x.PeriodEnd })
            .HasDatabaseName("ix_ai_summaries_organization_branch_category_period");

        // Serves the per-subject daily limit of AI-011, which reads across periods and therefore
        // cannot use the index above.
        builder.HasIndex(x => new { x.OrganizationId, x.Scope, x.SubjectUserId, x.LastForcedAt })
            .HasDatabaseName("ix_ai_summaries_forced");

        builder.HasIndex(x => x.CreatedAt)
            .HasDatabaseName("ix_ai_summaries_created_at");
    }
}
