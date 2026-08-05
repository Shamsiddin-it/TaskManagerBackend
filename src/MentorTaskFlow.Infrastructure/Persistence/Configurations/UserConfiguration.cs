using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        // The table is `users`, not `user`: USER is a reserved word in PostgreSQL and would need
        // quoting everywhere (DEPLOY-001).
        builder.ToTable("users", table =>
        {
            table.HasCheckConstraint(
                "ck_users_role_allowed",
                "role IN ('Admin','Lead','Mentor')");

            table.HasCheckConstraint(
                "ck_users_admin_scope_allowed",
                "admin_scope IS NULL OR admin_scope IN ('Organization','Branch')");

            table.HasCheckConstraint(
                "ck_users_role_admin_scope",
                "(role = 'Admin' AND admin_scope IS NOT NULL) OR (role <> 'Admin' AND admin_scope IS NULL)");

            table.HasCheckConstraint(
                "ck_users_role_category",
                "(role = 'Admin' AND category_id IS NULL) OR (role IN ('Lead','Mentor') AND category_id IS NOT NULL)");

            // The four permitted shapes of USER-023. This is the database half of the guarantee that
            // User.EnsureScopeShape implements in the domain; neither is sufficient alone (TEN-023).
            table.HasCheckConstraint(
                "ck_users_scope_shape",
                """
                (role = 'Admin' AND admin_scope = 'Organization' AND branch_id IS NULL AND category_id IS NULL)
                OR (role = 'Admin' AND admin_scope = 'Branch' AND branch_id IS NOT NULL AND category_id IS NULL)
                OR (role IN ('Lead','Mentor') AND branch_id IS NOT NULL AND category_id IS NOT NULL)
                """);
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.FullName).HasMaxLength(User.FullNameMaxLength).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(User.EmailMaxLength).IsRequired();
        builder.Property(x => x.NormalizedEmail).HasMaxLength(User.EmailMaxLength).IsRequired();

        // `text`, not varchar: the hash format may change with the algorithm (TZ 11.2).
        builder.Property(x => x.PasswordHash).HasColumnType("text");

        // Enums are varchar carrying the C# member name, guarded by a CHECK. Native PostgreSQL ENUM
        // types are not used: adding a value needs ALTER TYPE outside the migration transaction and
        // complicates rollback (DEPLOY-004).
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.AdminScope).HasConversion<string>().HasMaxLength(16);

        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.TelegramChatId).HasMaxLength(32);
        builder.Property(x => x.TokenVersion).IsRequired().HasDefaultValue(0);
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.FailedLoginCount).IsRequired().HasDefaultValue(0);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .HasConstraintName("fk_users_organization")
            .OnDelete(DeleteBehavior.Restrict);

        // fk_users_branch_scope: a user in a branch of a foreign organization becomes impossible.
        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(x => new { x.BranchId, x.OrganizationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId })
            .HasConstraintName("fk_users_branch_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // fk_users_category_scope: «a Lead of branch A attached to a category of branch B» becomes
        // physically impossible, not merely rejected by validation (USER-024).
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => new { x.CategoryId, x.OrganizationId, x.BranchId })
            .HasPrincipalKey(x => new { x.Id, x.OrganizationId, x.BranchId })
            .HasConstraintName("fk_users_category_scope")
            .OnDelete(DeleteBehavior.Restrict);

        // Globally unique, deliberately not per organization — see TEN-028 and User.NormalizedEmail.
        builder.HasIndex(x => x.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("ux_users_normalized_email");

        builder.HasIndex(x => x.TelegramChatId)
            .IsUnique()
            .HasFilter("telegram_chat_id IS NOT NULL")
            .HasDatabaseName("ux_users_telegram_chat_id");

        // At most one active Lead per category. Because a category belongs to exactly one branch,
        // the constraint is branch-scoped automatically (USER-003, TZ 12.1).
        builder.HasIndex(x => x.CategoryId)
            .IsUnique()
            .HasFilter("role = 'Lead' AND is_active = true")
            .HasDatabaseName("ux_users_active_lead_per_category");

        // Target of the composite FKs from assignments (assignee, assigner) and reviews (reviewer).
        builder.HasIndex(x => new { x.Id, x.OrganizationId, x.BranchId, x.CategoryId })
            .IsUnique()
            .HasDatabaseName("ux_users_id_scope");

        // Tenant-leading indexes: every list query must use one (TEN-029, PERF-006).
        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.Role, x.IsActive })
            .HasDatabaseName("ix_users_organization_branch_role_is_active");

        builder.HasIndex(x => new { x.OrganizationId, x.BranchId, x.CategoryId, x.Role, x.IsActive })
            .HasDatabaseName("ix_users_organization_branch_category_role_is_active");

        builder.HasIndex(x => new { x.OrganizationId, x.Role, x.AdminScope, x.IsActive })
            .HasDatabaseName("ix_users_organization_role_admin_scope_is_active");

        builder.ApplyConcurrencyToken();
    }
}
