using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class TelegramBindTokenConfiguration : IEntityTypeConfiguration<TelegramBindToken>
{
    public void Configure(EntityTypeBuilder<TelegramBindToken> builder)
    {
        builder.ToTable("telegram_bind_tokens");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.TokenHash).HasColumnType("char(64)").IsRequired();
        builder.Property(x => x.ExpiresAt).IsRequired();
        builder.Property(x => x.UsedAt);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // TG-006 and scenario 13 of Приложение K: one live token per user, decided by the index. Two
        // simultaneous requests would both find none to invalidate, and the loser is refused rather
        // than leaving two working links to one account.
        builder.HasIndex(x => x.UserId)
            .IsUnique()
            .HasFilter("used_at IS NULL")
            .HasDatabaseName("ux_telegram_bind_tokens_active");

        // The lookup path of /start: find by hash, then compare constant-time. The index makes the
        // first step cheap without making the second one skippable.
        builder.HasIndex(x => x.TokenHash)
            .HasDatabaseName("ix_telegram_bind_tokens_token_hash");

        // Retention removes spent and expired rows after 30 days (27.5).
        builder.HasIndex(x => x.ExpiresAt)
            .HasDatabaseName("ix_telegram_bind_tokens_expires_at");

        // No tenant columns and no query filter: the row is reached only through UserId and takes part
        // in no list or analytical query (TEN-009, Приложение M.1).
    }
}
