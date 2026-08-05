using MentorTaskFlow.Domain.Categories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MentorTaskFlow.Infrastructure.Persistence.Configurations;

public sealed class CategorySettingsConfiguration : IEntityTypeConfiguration<CategorySettings>
{
    public void Configure(EntityTypeBuilder<CategorySettings> builder)
    {
        builder.ToTable("category_settings", table =>
        {
            table.HasCheckConstraint(
                "ck_category_settings_due_days",
                $"default_assignment_due_days BETWEEN {CategorySettings.MinDueDays} AND {CategorySettings.MaxDueDays}");

            // 0 is rejected deliberately: disabling reminders is not a supported configuration
            // (NTF-007). Users mute them with Telegram unbinding or mail filters instead.
            table.HasCheckConstraint(
                "ck_category_settings_reminder_hours",
                $"deadline_reminder_hours BETWEEN {CategorySettings.MinReminderHours} AND {CategorySettings.MaxReminderHours}");
        });

        // The primary key is the category id: a category has exactly one settings row, created in
        // the same transaction (CAT-014).
        builder.HasKey(x => x.CategoryId);

        builder.Property(x => x.CategoryId).ValueGeneratedNever();
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.Property(x => x.BranchId).IsRequired();
        builder.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DefaultAssignmentDueDays).IsRequired();
        builder.Property(x => x.DefaultDueTimeLocal).IsRequired();
        builder.Property(x => x.DeadlineReminderHours).IsRequired();
        builder.Property(x => x.AllowLateSubmission).IsRequired();

        // fk_category_settings_scope: settings can never carry a scope different from their category.
        builder.HasOne<Category>()
            .WithOne()
            .HasForeignKey<CategorySettings>(x => new { x.CategoryId, x.OrganizationId, x.BranchId })
            .HasPrincipalKey<Category>(x => new { x.Id, x.OrganizationId, x.BranchId })
            .HasConstraintName("fk_category_settings_scope")
            .OnDelete(DeleteBehavior.Restrict);

        builder.ApplyConcurrencyToken();
    }
}
