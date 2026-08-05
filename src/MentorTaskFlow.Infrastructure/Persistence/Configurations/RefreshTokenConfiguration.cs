using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();

        // char(64): lowercase hex SHA-256 (TZ 11.2). Fixed width, so PostgreSQL stores no length
        // header and the equality lookup below stays cheap.
        builder.Property(x => x.TokenHash).HasColumnType("char(64)").IsRequired();

        builder.Property(x => x.FamilyId).IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.CreatedByIp).HasMaxLength(45);
        builder.Property(x => x.RevokedByIp).HasMaxLength(45);
        builder.Property(x => x.ReasonRevoked).HasConversion<string>().HasMaxLength(24);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .HasConstraintName("fk_refresh_tokens_user")
            .OnDelete(DeleteBehavior.Restrict);

        // Not unique: a hash collision is not the threat model, and a unique index here would turn an
        // astronomically unlikely collision into a failed login instead of a rejected token.
        builder.HasIndex(x => x.TokenHash)
            .HasDatabaseName("ix_refresh_tokens_token_hash");

        // Revoking a whole family on reuse detection is a single indexed sweep (AUTH-008).
        builder.HasIndex(x => new { x.UserId, x.FamilyId })
            .HasDatabaseName("ix_refresh_tokens_user_family");

        // No tenant scope columns: these rows are reached only through UserId and take part in no
        // list or analytical query with a tenant filter (TEN-009, Приложение M.1).
    }
}
