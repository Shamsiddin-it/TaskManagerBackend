using MentorTaskFlow.Domain.Reviews;
using MentorTaskFlow.Domain.Submissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> builder)
    {
        builder.ToTable("reviews", table =>
        {
            // 12.2: the two decisions carry different mandatory fields, and the database is where that
            // stays true regardless of which code path wrote the row.
            table.HasCheckConstraint(
                "ck_reviews_decision_fields",
                "(decision = 'NeedsRework' AND char_length(comment) BETWEEN 10 AND 3000 AND rework_due_at IS NOT NULL) "
                + "OR (decision = 'Approved' AND rework_due_at IS NULL)");

            table.HasCheckConstraint("ck_reviews_decision_allowed", "decision IN ('Approved','NeedsRework')");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.SubmissionId).IsRequired();
        builder.Property(x => x.AssignmentId).IsRequired();
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.CategoryId).IsRequired();
        builder.Property(x => x.ReviewerId).IsRequired();
        builder.Property(x => x.Decision).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.Comment).HasMaxLength(Review.CommentMaxLength);
        builder.Property(x => x.ReworkDueAt);

        // fk_reviews_submission_scope (constraint 14 of 12.2a). Safe as a foreign key: a submission's
        // scope is copied from its assignment and never moves.
        builder.HasOne<Submission>()
            .WithMany()
            .HasForeignKey(x => new { x.SubmissionId, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasConstraintName("fk_reviews_submission_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // REV-005: one review per submission, decided by the index rather than by a prior lookup —
        // two concurrent decisions would both find none and both write.
        builder.HasIndex(x => x.SubmissionId)
            .IsUnique()
            .HasDatabaseName("ux_reviews_submission");

        builder.HasIndex(x => x.AssignmentId)
            .HasDatabaseName("ix_reviews_assignment_id");

        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.CategoryId, x.CreatedAt })
            .HasDatabaseName("ix_reviews_scope_created_at")
            .IsDescending(false, false, false, true);

        // Named explicitly: EF's own name for a four-column foreign-key index is truncated at the
        // PostgreSQL identifier limit and unreadable in a plan (11.1).
        builder.HasIndex(x => new { x.SubmissionId, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasDatabaseName("ix_reviews_submission_scope");

        // Append-only (REV-020): no concurrency token and no UpdatedAt. The reviewer's scope FK is not
        // mapped here — see the migration for why it is a trigger instead.
    }
}
