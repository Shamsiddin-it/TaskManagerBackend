using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class UserSecurityTokenConfiguration : IEntityTypeConfiguration<UserSecurityToken>
{
    public void Configure(EntityTypeBuilder<UserSecurityToken> builder)
    {
        builder.ToTable("user_security_tokens", table =>
        {
            table.HasCheckConstraint(
                "ck_user_security_tokens_purpose_allowed",
                "purpose IN ('SetPassword','ResetPassword')");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.TokenHash).HasColumnType("char(64)").IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.CreatedByIp).HasMaxLength(45);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_user_security_tokens_user")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.TokenHash)
            .HasDatabaseName("ix_user_security_tokens_token_hash");

        // At most one live token per purpose per user (AUTH-030). A partial unique index rather than a
        // service check: issuing a new link and invalidating the old one is a two-statement operation,
        // and two concurrent invitations would otherwise both succeed and leave two working links.
        builder.HasIndex(x => new { x.UserId, x.Purpose })
            .IsUnique()
            .HasFilter("used_at IS NULL AND invalidated_at IS NULL")
            .HasDatabaseName("ux_user_security_tokens_active");
    }
}
