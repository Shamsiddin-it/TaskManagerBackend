using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Submissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class SubmissionConfiguration : IEntityTypeConfiguration<Submission>
{
    public void Configure(EntityTypeBuilder<Submission> builder)
    {
        builder.ToTable("submissions", table =>
        {
            table.HasCheckConstraint("ck_submissions_version_positive", "version_number >= 1");

            // SUB-010 and SUB-015 as one rule: an empty file and an oversized one are both refused
            // long before this, but the column is where the guarantee outlives the code that made it.
            table.HasCheckConstraint(
                "ck_submissions_size_bounds",
                "file_size_bytes > 0 AND file_size_bytes <= 52428800");

            table.HasCheckConstraint("ck_submissions_extension_allowed", "file_extension IN ('Pdf','Pptx')");

            // Lower-case hex, exactly 64 characters. Mixed case would make the duplicate check of
            // SUB-028 miss a byte-identical file (10.7).
            table.HasCheckConstraint("ck_submissions_sha256_format", "sha256_hash ~ '^[0-9a-f]{64}$'");

            // 17.5: in Release 1.0 a PDF previews as itself and a PPTX has no preview at all. Anything
            // else would mean a preview pointing at a different object than the one submitted.
            table.HasCheckConstraint(
                "ck_submissions_preview_key",
                "(file_extension = 'Pdf' AND preview_storage_key = storage_key) "
                + "OR (file_extension = 'Pptx' AND preview_storage_key IS NULL)");

            // Reserved for the asynchronous conversion of a later version; a value here now would be a
            // state nothing in Release 1.0 can produce or interpret.
            table.HasCheckConstraint("ck_submissions_conversion_status", "conversion_status IS NULL");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.AssignmentId).IsRequired();
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.CategoryId).IsRequired();
        builder.Property(x => x.VersionNumber).IsRequired();
        builder.Property(x => x.StorageKey).HasMaxLength(Submission.StorageKeyMaxLength).IsRequired();
        builder.Property(x => x.OriginalFileName).HasMaxLength(Submission.OriginalFileNameMaxLength).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.FileExtension).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(x => x.FileSizeBytes).IsRequired();
        // Named explicitly: the convention turns Sha256Hash into sha256hash, which would not match the
        // check constraint above — and a mismatch there fails the migration, not the request.
        builder.Property(x => x.Sha256Hash).HasColumnName("sha256_hash").HasColumnType("char(64)").IsRequired();
        builder.Property(x => x.IsLate).IsRequired();
        builder.Property(x => x.SubmittedById).IsRequired();
        builder.Property(x => x.SubmittedAt).IsRequired();
        builder.Property(x => x.PreviewStorageKey).HasMaxLength(Submission.StorageKeyMaxLength);
        builder.Property(x => x.ConversionStatus).HasMaxLength(16);

        // fk_submissions_assignment_scope: a submission whose scope differs from its assignment becomes
        // physically impossible (constraint 13 of 12.2a). Unlike the executor constraints this one is
        // safe as a foreign key — an assignment's scope is an immutable snapshot and never moves.
        builder.HasOne<Assignment>()
            .WithMany()
            .HasForeignKey(x => new { x.AssignmentId, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasConstraintName("fk_submissions_assignment_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // Named rather than left to EF, whose generated name for a four-column foreign key is truncated
        // at the PostgreSQL identifier limit and unrecognisable in an execution plan (11.1).
        builder.HasIndex(x => new { x.AssignmentId, x.OrganizationId, x.BranchId, x.CategoryId })
            .HasDatabaseName("ix_submissions_assignment_scope");

        // The final arbiter of version numbering: two concurrent uploads both computing MAX+1 would
        // agree, and one has to lose rather than both writing version 2 (12.5).
        builder.HasIndex(x => new { x.AssignmentId, x.VersionNumber })
            .IsUnique()
            .HasDatabaseName("ux_submissions_assignment_version");

        // SUB-027: deliberately NOT unique. The same correct file may legitimately appear under
        // different assignments — a shared presentation template, for one. Only a repeat within one
        // assignment is refused, and that is a service check, not an index.
        builder.HasIndex(x => x.Sha256Hash)
            .HasDatabaseName("ix_submissions_sha256_hash");

        // The target of Review's composite FK (constraint 14) is the alternate key EF declares for the
        // scope tuple. A hand-written unique index on the same four columns stood here until Phase 11
        // and was removed as an exact duplicate of it.
        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.CategoryId, x.SubmittedAt })
            .HasDatabaseName("ix_submissions_scope_submitted_at")
            .IsDescending(false, false, false, true);

        // Append-only (SUB-020): no concurrency token, and no UpdatedAt.
    }
}
