using System.Text.Json;
using Hangfire;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MentorTaskFlow.Infrastructure.Scheduling;

/// <summary>
/// Produces the day's suggestions for every category in one time zone (TZ 20.2, 20.3).
/// </summary>
/// <remarks>
/// Registered per unique <c>CategorySettings.TimeZoneId</c> with cron <c>0 6 * * *</c> in that zone
/// (<c>SCH-001</c>): six in the morning means six in the morning where the category is, not where the
/// server happens to run.
/// </remarks>
public sealed class AutoGenerationJob(
    MentorTaskFlowDbContext dbContext,
    MentorSelector mentorSelector,
    IDeadlineCalculator deadlines,
    IOutboxWriter outboxWriter,
    IAuditWriter auditWriter,
    SchedulerMetrics metrics,
    ILogger<AutoGenerationJob> logger,
    IClock clock)
{
    /// <summary>
    /// Runs the chain of <c>SCH-002</c> for one time zone.
    /// </summary>
    /// <remarks>
    /// <c>DisableConcurrentExecution</c> keeps two runs of the same zone apart at the scheduler level;
    /// the idempotency key keeps the data correct even if that ever fails (<c>TEN-054</c>).
    /// </remarks>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(string timeZoneId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var localDate = DateOnly.FromDateTime(deadlines.ToLocal(now, timeZoneId).DateTime);

        logger.LogInformation(
            "Auto-generation started for {TimeZone}, local date {LocalDate}.",
            timeZoneId,
            localDate);

        // The chain, top down, breaking at the first inactive link (TEN-050). Written as one query so
        // no step can be forgotten at a call site, and so the tenant filter is inherent rather than
        // remembered.
        var targets = await dbContext.Categories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.IsActive)
            .Join(
                dbContext.CategorySettings.IgnoreQueryFilters().AsNoTracking()
                    .Where(s => s.TimeZoneId == timeZoneId),
                c => c.Id,
                s => s.CategoryId,
                (c, s) => new { Category = c, Settings = s })
            .Join(
                dbContext.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.IsActive),
                x => x.Category.BranchId,
                b => b.Id,
                (x, b) => new { x.Category, x.Settings })
            .Join(
                dbContext.Organizations.IgnoreQueryFilters().AsNoTracking().Where(o => o.IsActive),
                x => x.Category.OrganizationId,
                o => o.Id,
                (x, o) => new CategoryTarget(
                    x.Category.OrganizationId,
                    x.Category.BranchId,
                    x.Category.Id,
                    x.Settings.DefaultAssignmentDueDays,
                    x.Settings.DefaultDueTimeLocal,
                    x.Settings.TimeZoneId))
            .ToListAsync(cancellationToken);

        foreach (var target in targets)
        {
            await ProcessCategoryAsync(target, localDate, now, cancellationToken);
        }
    }

    private async Task ProcessCategoryAsync(
        CategoryTarget target,
        DateOnly localDate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var templates = await dbContext.TopicAssignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.CategoryId == target.CategoryId && t.IsActive && t.IsRequired)
            .Join(
                dbContext.Topics.IgnoreQueryFilters().AsNoTracking()
                    .Where(t => t.IsActive && t.PlannedDate == localDate),
                a => a.TopicId,
                t => t.Id,
                (a, t) => new { Template = a, Topic = t })
            .ToListAsync(cancellationToken);

        if (templates.Count == 0)
        {
            return;
        }

        // SCH-016: no active mentor is not a job failure. The suggestions simply cannot be addressed,
        // and the people who can fix that are told.
        var mentorId = await mentorSelector.SelectAsync(
            target.OrganizationId,
            target.BranchId,
            target.CategoryId,
            cancellationToken);

        if (mentorId is null)
        {
            await ReportNoMentorAsync(target, localDate, cancellationToken);
            metrics.Skipped("no_active_mentor");

            return;
        }

        var dueAt = deadlines.CalculateInitialDueAt(
            localDate,
            target.DueDays,
            target.DueTimeLocal,
            target.TimeZoneId);

        foreach (var item in templates)
        {
            var suggestion = Assignment.CreateSuggestion(
                target.OrganizationId,
                target.BranchId,
                target.CategoryId,
                mentorId.Value,
                item.Template.Id,
                item.Template.Title,
                item.Template.Description,
                dueAt,
                localDate,
                AutoGenerationKey.For(
                    target.OrganizationId,
                    target.BranchId,
                    target.CategoryId,
                    item.Template.Id,
                    localDate),
                now);

            await PersistAsync(suggestion, target, now, cancellationToken);
        }
    }

    /// <summary>
    /// Writes one suggestion, skipping it if the day's key already exists.
    /// </summary>
    /// <remarks>
    /// <c>SCH-010</c> and <c>SCH-011</c>: the conflict is the whole idempotency mechanism. A second
    /// run of the day must not resurrect a suggestion the Lead has already accepted or rejected, and
    /// the key guarantees the skip whatever the current status of that task is.
    /// </remarks>
    private async Task PersistAsync(
        Assignment suggestion,
        CategoryTarget target,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        dbContext.Assignments.Add(suggestion);

        dbContext.TaskEvents.Add(TaskEvent.Record(
            suggestion,
            TaskEventType.SuggestedCreated,

            // A system action has no actor, and TaskEvent refuses one for this type (10.9).
            actorId: null,
            previousStatus: null,
            AssignmentStatus.Suggested,
            Guid.CreateVersion7(),
            now,
            JsonSerializer.SerializeToDocument(new
            {
                topicAssignmentId = suggestion.TopicAssignmentId,
                generatedForDate = suggestion.GeneratedForDate,
            })));

        await NotifyLeadAsync(suggestion, target, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            metrics.SuggestionCreated(target.BranchId);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                                                  {
                                                      SqlState: PostgresErrorCodes.UniqueViolation,
                                                      ConstraintName: "ux_assignments_auto_generation_key_scoped",
                                                  })
        {
            // Already generated today. Detach the whole unit so the next template starts clean.
            dbContext.ChangeTracker.Clear();
            metrics.DuplicateSkipped(target.BranchId);
        }
    }

    /// <summary>
    /// <c>SCH-018</c>: with no active Lead the suggestions still accumulate — there is simply nobody to
    /// tell. The gap itself is reported by <c>CategoryWithoutLead</c> when the Lead was deactivated.
    /// </summary>
    private async Task NotifyLeadAsync(
        Assignment suggestion,
        CategoryTarget target,
        CancellationToken cancellationToken)
    {
        var leadId = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.CategoryId == target.CategoryId && u.Role == UserRole.Lead && u.IsActive)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (leadId is not { } recipient)
        {
            return;
        }

        await outboxWriter.EnqueueSystemAsync(
            new OutboxEntry
            {
                RecipientUserId = recipient,
                EventType = NotificationEventTypes.AssignmentSuggested,
                EntityId = suggestion.Id,
                CategoryId = target.CategoryId,
                Payload = JsonSerializer.SerializeToDocument(new
                {
                    assignmentTitle = suggestion.Title,
                    currentDueAt = suggestion.CurrentDueAt,
                }),
            },
            target.OrganizationId,
            target.BranchId,
            cancellationToken);
    }

    /// <summary>
    /// <c>SCH-016</c> and <c>SCH-017</c>: recorded once, and told to the people who can act — at most
    /// once a day per category.
    /// </summary>
    private async Task ReportNoMentorAsync(
        CategoryTarget target,
        DateOnly localDate,
        CancellationToken cancellationToken)
    {
        logger.LogWarning("Auto-generation found no active mentor in a category.");

        auditWriter.WriteSystem(
            new AuditEntry
            {
                Action = AuditActions.SchedulerNoActiveMentor,
                EntityType = "Category",
                EntityId = target.CategoryId,
                CategoryId = target.CategoryId,
                Result = AuditResult.Failure,
                FailureReason = "no_active_mentor",
            },
            target.OrganizationId,
            target.BranchId);

        // TEN-044: the Lead of this category, the administrator of this branch, and the organization
        // administrators — and nobody from another branch (TEN-045).
        var recipients = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.OrganizationId == target.OrganizationId && u.IsActive)
            .Where(u => (u.Role == UserRole.Lead && u.CategoryId == target.CategoryId)
                        || (u.Role == UserRole.Admin
                            && (u.AdminScope == AdminScope.Organization
                                || u.BranchId == target.BranchId)))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        foreach (var recipientId in recipients)
        {
            await outboxWriter.EnqueueSystemAsync(
                new OutboxEntry
                {
                    RecipientUserId = recipientId,
                    EventType = NotificationEventTypes.SchedulerNoActiveMentor,
                    EntityId = target.CategoryId,

                    // The local date bounds it to one message a day per category (SCH-017).
                    Discriminator = $"{localDate:yyyy-MM-dd}:{recipientId:N}",
                    CategoryId = target.CategoryId,
                    Payload = JsonSerializer.SerializeToDocument(new { localDate = localDate.ToString("yyyy-MM-dd") }),
                },
                target.OrganizationId,
                target.BranchId,
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed record CategoryTarget(
        Guid OrganizationId,
        Guid BranchId,
        Guid CategoryId,
        int DueDays,
        TimeOnly DueTimeLocal,
        string TimeZoneId);
}
