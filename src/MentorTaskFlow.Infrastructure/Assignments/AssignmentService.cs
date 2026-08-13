using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Assignments;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Tenancy;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Persistence;
using MentorTaskFlow.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.Infrastructure.Assignments;

/// <inheritdoc />
public sealed class AssignmentService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    ITenantStateGuard tenantState,
    IAuditWriter auditWriter,
    IOutboxWriter outboxWriter,
    IRequestContext requestContext,
    IDeadlineCalculator deadlines,
    IClock clock) : IAssignmentService
{
    private static readonly string[] SortableColumns = ["currentDueAt", "createdAt", "status"];

    // -----------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------

    public async Task<PagedResult<AssignmentDto>> ListAsync(AssignmentListQuery query, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        var page = Math.Max(query.Page, PaginationLimits.DefaultPage);
        var pageSize = Math.Clamp(query.PageSize, PaginationLimits.MinPageSize, PaginationLimits.MaxPageSize);

        var source = ApplyVisibility(
            dbContext.Assignments.AsNoTracking().Where(a => a.OrganizationId == branchContext.EffectiveOrganizationId),
            actor);

        if (!string.IsNullOrWhiteSpace(query.Status) && Enum.TryParse<AssignmentStatus>(query.Status, out var status))
        {
            source = source.Where(a => a.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Source) && Enum.TryParse<AssignmentSource>(query.Source, out var assignmentSource))
        {
            source = source.Where(a => a.Source == assignmentSource);
        }

        if (query.CategoryId is { } categoryId)
        {
            source = source.Where(a => a.CategoryId == categoryId);
        }

        // Applied after the ownership narrowing above, so for a Mentor it can only ever shrink the
        // result further — never widen it past their own tasks.
        if (query.AssignedToId is { } assignedToId && actor.Role is not UserRole.Mentor)
        {
            source = source.Where(a => a.AssignedToId == assignedToId);
        }

        source = ApplySort(source, query);

        var totalCount = await source.CountAsync(cancellationToken);

        var rows = await source
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AssignmentRow(a, EF.Property<uint>(a, ConcurrencyTokenExtensions.PropertyName)))
            .ToListAsync(cancellationToken);

        var branches = branchContext.IsAllBranchesReadContext
            ? await LoadBranchSummariesAsync(rows.Select(r => r.Assignment.BranchId), cancellationToken)
            : [];

        var items = rows
            .Select(row => ToDto(
                row.Assignment,
                ConcurrencyTokenAccessor.EncodeFrom(row.Xmin),
                branches.GetValueOrDefault(row.Assignment.BranchId)))
            .ToList();

        return new PagedResult<AssignmentDto>(items, page, pageSize, totalCount);
    }

    public async Task<AssignmentDto> GetAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var row = await FindReadableAsync(assignmentId, cancellationToken);

        return ToDto(row.Assignment, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin), null);
    }

    public async Task<IReadOnlyList<TaskEventDto>> GetHistoryAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        await FindReadableAsync(assignmentId, cancellationToken);

        var events = await dbContext.TaskEvents
            .AsNoTracking()
            .Where(e => e.AssignmentId == assignmentId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync(cancellationToken);

        // EVT-004: a Mentor sees the role of whoever acted, not their identity. The history has to be
        // readable — it explains what happened to their task — without naming other people.
        var hideActor = actor.Role is UserRole.Mentor;

        return events
            .Select(e => new TaskEventDto(
                e.Id,
                e.SequenceNumber,
                e.EventType.ToString(),
                hideActor ? null : e.ActorId,
                hideActor ? ActorLabel(e) : null,
                e.PreviousStatus?.ToString(),
                e.NewStatus?.ToString(),
                e.OccurredAt,
                e.CorrelationId))
            .ToList();
    }

    // -----------------------------------------------------------------
    // Transitions
    // -----------------------------------------------------------------

    public async Task<AssignmentDto> CreateDraftAsync(CreateAssignmentDraftRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireLead();
        var categoryId = actor.CategoryId!.Value;

        await tenantState.EnsureWritableAsync(actor.BranchId, categoryId, cancellationToken);
        await EnsureAssigneeIsUsableAsync(request.AssignedToId, categoryId, cancellationToken);

        var template = await ResolveTemplateAsync(request.TopicAssignmentId, categoryId, cancellationToken);

        // 10.6.1: the template's title and description are copied now and stop tracking it. A later
        // edit of the template must not rewrite work already handed out.
        var title = request.Title ?? template?.Title
            ?? throw new ValidationAppException("title", "Название обязательно, если не указан шаблон задания.");

        var description = request.Description ?? template?.Description;

        var dueAt = await ResolveDueAtAsync(request.DueAt, categoryId, cancellationToken);
        var now = clock.UtcNow;

        var assignment = Assignment.CreateDraft(
            branchContext.EffectiveOrganizationId,
            actor.BranchId!.Value,
            categoryId,
            request.AssignedToId,
            actor.UserId,
            request.TopicAssignmentId,
            title,
            description,
            dueAt,
            now);

        dbContext.Assignments.Add(assignment);

        RecordEvent(assignment, TaskEventType.DraftCreated, actor.UserId, null, AssignmentStatus.Draft, now,
            JsonSerializer.SerializeToDocument(new
            {
                assignedToId = assignment.AssignedToId,
                topicAssignmentId = assignment.TopicAssignmentId,
                initialDueAt = assignment.InitialDueAt,
            }));

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(assignment, dbContext.Read(assignment), null);
    }

    public async Task<AssignmentDto> UpdateAsync(Guid assignmentId, UpdateAssignmentRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireLead();
        var assignment = await FindWritableAsync(assignmentId, cancellationToken);
        dbContext.Expect(assignment, request.ConcurrencyToken);

        if (assignment.AssignedToId != request.AssignedToId)
        {
            await EnsureAssigneeIsUsableAsync(request.AssignedToId, assignment.CategoryId, cancellationToken);
            assignment.Reassign(request.AssignedToId, hasSubmissions: false, clock.UtcNow);
        }

        assignment.Edit(request.Title, request.Description, request.DueAt, clock.UtcNow);

        await dbContext.SaveWithConcurrencyCheckAsync(assignment, cancellationToken);

        return ToDto(assignment, dbContext.Read(assignment), null);
    }

    public async Task<AssignmentDto> PublishAsync(Guid assignmentId, AssignmentActionRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireLead();
        var assignment = await FindWritableAsync(assignmentId, cancellationToken);
        dbContext.Expect(assignment, request.ConcurrencyToken);

        // ASN-008: publishing onto a deactivated mentor would hand work to somebody who cannot sign in.
        await EnsureAssigneeIsUsableAsync(assignment.AssignedToId, assignment.CategoryId, cancellationToken);

        var now = clock.UtcNow;
        var previous = assignment.Status;

        assignment.Publish(actor.UserId, now);

        RecordEvent(assignment, TaskEventType.Assigned, actor.UserId, previous, assignment.Status, now,
            JsonSerializer.SerializeToDocument(new
            {
                assignedById = actor.UserId,
                assignedToId = assignment.AssignedToId,
                currentDueAt = assignment.CurrentDueAt,
            }));

        NotifyAssignee(assignment, NotificationEventTypes.AssignmentAssigned, $"assigned:{assignment.Id:N}");

        await dbContext.SaveWithConcurrencyCheckAsync(assignment, cancellationToken);

        return ToDto(assignment, dbContext.Read(assignment), null);
    }

    public async Task<AssignmentDto> AcceptSuggestionAsync(Guid assignmentId, AssignmentActionRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireLead();
        var assignment = await FindWritableAsync(assignmentId, cancellationToken);
        dbContext.Expect(assignment, request.ConcurrencyToken);

        await EnsureAssigneeIsUsableAsync(assignment.AssignedToId, assignment.CategoryId, cancellationToken);

        var now = clock.UtcNow;
        var previous = assignment.Status;

        assignment.AcceptSuggestion(actor.UserId, now);

        // EVT-005: two events for one transition, sharing a correlation id and taking consecutive
        // sequence numbers. The Lead's decision and the publication are separate facts a review may
        // need to tell apart.
        RecordEvent(assignment, TaskEventType.SuggestionAccepted, actor.UserId, previous, previous, now,
            JsonSerializer.SerializeToDocument(new { acceptedById = actor.UserId, edited = false }));

        RecordEvent(assignment, TaskEventType.Assigned, actor.UserId, previous, assignment.Status, now,
            JsonSerializer.SerializeToDocument(new
            {
                assignedById = actor.UserId,
                assignedToId = assignment.AssignedToId,
                currentDueAt = assignment.CurrentDueAt,
            }));

        NotifyAssignee(assignment, NotificationEventTypes.AssignmentAssigned, $"assigned:{assignment.Id:N}");

        await dbContext.SaveWithConcurrencyCheckAsync(assignment, cancellationToken);

        return ToDto(assignment, dbContext.Read(assignment), null);
    }

    public async Task<AssignmentDto> ReassignAsync(Guid assignmentId, ReassignAssignmentRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireLead();
        var assignment = await FindWritableAsync(assignmentId, cancellationToken);
        dbContext.Expect(assignment, request.ConcurrencyToken);

        await EnsureAssigneeIsUsableAsync(request.AssignedToId, assignment.CategoryId, cancellationToken);

        // 10.6.3: after the first submission the executor is fixed. Reassigning would leave one
        // person's work attached to a task now owned by another.
        var hasSubmissions = assignment.FirstSubmittedAt is not null;

        var previousAssigneeId = assignment.AssignedToId;
        var now = clock.UtcNow;
        var wasPublished = assignment.Status is AssignmentStatus.Assigned;

        assignment.Reassign(request.AssignedToId, hasSubmissions, now);

        // NewStatus is null: reassignment changes the executor, not the state of the work.
        RecordEvent(assignment, TaskEventType.Reassigned, actor.UserId, assignment.Status, null, now,
            JsonSerializer.SerializeToDocument(new
            {
                previousAssignedToId = previousAssigneeId,
                newAssignedToId = assignment.AssignedToId,
                reason = request.Reason,
            }));

        // Only a published task has an executor who knows about it; nobody needs telling that a draft
        // changed hands (Приложение B).
        if (wasPublished)
        {
            NotifyUser(assignment, previousAssigneeId, NotificationEventTypes.AssignmentReassigned,
                $"reassigned:{assignment.Id:N}:{assignment.LastEventSequence}:{previousAssigneeId:N}");

            NotifyAssignee(assignment, NotificationEventTypes.AssignmentReassigned,
                $"reassigned:{assignment.Id:N}:{assignment.LastEventSequence}:{assignment.AssignedToId:N}");
        }

        await dbContext.SaveWithConcurrencyCheckAsync(assignment, cancellationToken);

        return ToDto(assignment, dbContext.Read(assignment), null);
    }

    public async Task<AssignmentDto> StartReviewAsync(Guid assignmentId, AssignmentActionRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireLead();
        var assignment = await FindWritableAsync(assignmentId, cancellationToken);
        dbContext.Expect(assignment, request.ConcurrencyToken);

        var now = clock.UtcNow;
        var previous = assignment.Status;

        assignment.StartReview(now);

        RecordEvent(assignment, TaskEventType.ReviewStarted, actor.UserId, previous, assignment.Status, now,
            JsonSerializer.SerializeToDocument(new { reviewerId = actor.UserId }));

        await dbContext.SaveWithConcurrencyCheckAsync(assignment, cancellationToken);

        return ToDto(assignment, dbContext.Read(assignment), null);
    }

    public async Task<AssignmentDto> CancelAsync(Guid assignmentId, CancelAssignmentRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        var assignment = await FindCancellableAsync(actor, assignmentId, cancellationToken);
        dbContext.Expect(assignment, request.ConcurrencyToken);

        var now = clock.UtcNow;
        var previous = assignment.Status;
        var isForceCancel = actor.Role is UserRole.Admin;

        assignment.Cancel(actor.UserId, request.CancelReason, now);

        RecordEvent(assignment, TaskEventType.Cancelled, actor.UserId, previous, assignment.Status, now,
            JsonSerializer.SerializeToDocument(new
            {
                cancelledById = actor.UserId,
                cancelReason = assignment.CancelReason,
                forceCancel = isForceCancel,
            }));

        // The mentor is told only if they ever knew about the task; an unpublished draft was never
        // theirs (Приложение B, transitions 2 and 4).
        if (previous is not (AssignmentStatus.Draft or AssignmentStatus.Suggested))
        {
            NotifyAssignee(assignment, NotificationEventTypes.AssignmentCancelled, $"cancelled:{assignment.Id:N}");
        }

        // ASN-025: a force-cancel is the single administrative action inside the study cycle, so it is
        // audited even though ordinary lifecycle moves are not — those live in TaskEvent (AUD-004).
        if (isForceCancel)
        {
            auditWriter.Write(new AuditEntry
            {
                Action = AuditActions.AssignmentForceCancel,
                EntityType = nameof(Assignment),
                EntityId = assignment.Id,
                BranchId = assignment.BranchId,
                CategoryId = assignment.CategoryId,
                Metadata = JsonSerializer.SerializeToDocument(new
                {
                    previousStatus = previous.ToString(),
                    cancelReason = assignment.CancelReason,
                }),
            });
        }

        await dbContext.SaveWithConcurrencyCheckAsync(assignment, cancellationToken);

        return ToDto(assignment, dbContext.Read(assignment), null);
    }

    // -----------------------------------------------------------------
    // Events and notifications
    // -----------------------------------------------------------------

    /// <summary>
    /// Appends a TaskEvent to the current unit of work.
    /// </summary>
    /// <remarks>
    /// Staged rather than saved, so the status change, the event and any outbox rows commit in one
    /// transaction. A partial result is not acceptable: a status change without its event is a defect
    /// checked by <c>TEST-EVT-001</c> (<c>ASN-023</c>, <c>EVT-002</c>).
    /// </remarks>
    private void RecordEvent(
        Assignment assignment,
        TaskEventType eventType,
        Guid? actorId,
        AssignmentStatus? previousStatus,
        AssignmentStatus? newStatus,
        DateTimeOffset now,
        JsonDocument? metadata = null) =>
        dbContext.TaskEvents.Add(TaskEvent.Record(
            assignment,
            eventType,
            actorId,
            previousStatus,
            newStatus,
            requestContext.CorrelationId,
            now,
            metadata));

    private void NotifyAssignee(Assignment assignment, string eventType, string deduplicationKey) =>
        NotifyUser(assignment, assignment.AssignedToId, eventType, deduplicationKey);

    private void NotifyUser(Assignment assignment, Guid recipientId, string eventType, string deduplicationKey) =>
        outboxWriter.EnqueueSystem(
            new OutboxEntry
            {
                RecipientUserId = recipientId,
                EventType = eventType,
                Channel = NotificationChannel.Email,
                DeduplicationKey = deduplicationKey,
                CategoryId = assignment.CategoryId,

                // No identifiers beyond what the recipient already owns, and no deadline arithmetic:
                // the renderer formats the moment in the category's zone when it sends (UX-001).
                Payload = JsonSerializer.SerializeToDocument(new
                {
                    assignmentTitle = assignment.Title,
                    currentDueAt = assignment.CurrentDueAt,
                }),
            },
            assignment.OrganizationId,
            assignment.BranchId);

    // -----------------------------------------------------------------
    // Scope and guards
    // -----------------------------------------------------------------

    private ICurrentUserContext RequireActor() => currentUser.Current ?? throw new UnauthorizedException();

    /// <summary>
    /// The study cycle belongs to the Lead.
    /// </summary>
    /// <remarks>
    /// No endpoint of this group except <c>GET</c> and <c>cancel</c> is available to an administrator
    /// in either contour. Version 2.0's «Lead/Admin — управление» wrongly widened Admin's reach and
    /// was removed in 2.1 (<c>ASN-025</c>, TZ 23.4).
    /// </remarks>
    private ICurrentUserContext RequireLead()
    {
        var actor = RequireActor();

        return actor.Role is UserRole.Lead
            ? actor
            : throw new ForbiddenException(ErrorCodes.Forbidden, "Операция доступна только тимлиду категории.");
    }

    private static IQueryable<Assignment> ApplyVisibility(IQueryable<Assignment> source, ICurrentUserContext actor) => actor switch
    {
        // Level 4 of TZ 9.1: a Mentor sees their own tasks, and never the drafts or suggestions of
        // their category — a suggestion is a proposal the Lead has not yet accepted.
        { Role: UserRole.Mentor } => source.Where(a =>
            a.AssignedToId == actor.UserId
            && a.Status != AssignmentStatus.Draft
            && a.Status != AssignmentStatus.Suggested),

        { Role: UserRole.Lead } => source.Where(a => a.CategoryId == actor.CategoryId),
        { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => source.Where(a => a.BranchId == actor.BranchId),
        _ => source,
    };

    private async Task<AssignmentRow> FindReadableAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        return await ApplyVisibility(
                dbContext.Assignments
                    .AsNoTracking()
                    .Where(a => a.Id == assignmentId && a.OrganizationId == branchContext.EffectiveOrganizationId),
                actor)
            .Select(a => new AssignmentRow(a, EF.Property<uint>(a, ConcurrencyTokenExtensions.PropertyName)))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();
    }

    /// <summary>Loads a tracked assignment the Lead of its category may act on.</summary>
    private async Task<Assignment> FindWritableAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var actor = RequireLead();

        var assignment = await dbContext.Assignments.FirstOrDefaultAsync(
                a => a.Id == assignmentId && a.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        // 404 rather than 403: another category's task must be indistinguishable from one that does
        // not exist (TEN-006).
        if (assignment.CategoryId != actor.CategoryId)
        {
            throw new NotFoundException();
        }

        await tenantState.EnsureWritableAsync(assignment.BranchId, assignment.CategoryId, cancellationToken);

        return assignment;
    }

    /// <summary>
    /// Loads an assignment the caller may cancel — the Lead of its category, or an administrator
    /// within their contour.
    /// </summary>
    private async Task<Assignment> FindCancellableAsync(
        ICurrentUserContext actor,
        Guid assignmentId,
        CancellationToken cancellationToken)
    {
        if (actor.Role is UserRole.Mentor)
        {
            // The mentor can see their own task but cannot stop it: that decision belongs to whoever
            // handed the work out (TZ 9.3).
            throw new ForbiddenException(ErrorCodes.Forbidden, "Отмена задачи недоступна ментору.");
        }

        var assignment = await dbContext.Assignments.FirstOrDefaultAsync(
                a => a.Id == assignmentId && a.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        var reachable = actor switch
        {
            { Role: UserRole.Lead } => assignment.CategoryId == actor.CategoryId,
            { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => assignment.BranchId == actor.BranchId,

            // BRN-033: an Organization Admin must name the branch. A force-cancel is the one mutation
            // permitted inside a deactivated branch, and it has to be attributed to one.
            { Role: UserRole.Admin, AdminScope: AdminScope.Organization } =>
                assignment.BranchId == branchContext.RequireBranchForMutation(),

            _ => false,
        };

        if (!reachable)
        {
            throw new NotFoundException();
        }

        // Deliberately no EnsureWritableAsync: BRN-033 allows a force-cancel inside a deactivated
        // branch precisely so stuck work can be closed out.
        if (actor.Role is UserRole.Lead)
        {
            await tenantState.EnsureWritableAsync(assignment.BranchId, assignment.CategoryId, cancellationToken);
        }

        return assignment;
    }

    /// <summary>
    /// Verifies the executor is an active mentor of the same category (<c>ASN-008</c>).
    /// </summary>
    /// <remarks>
    /// A mentor from another branch or category answers 409 <c>CROSS_SCOPE_REFERENCE</c>, and the
    /// composite FK <c>fk_assignments_assignee_scope</c> refuses the same combination in the database
    /// — application validation is never the only guard (<c>TEN-025</c>).
    /// </remarks>
    private async Task EnsureAssigneeIsUsableAsync(Guid assigneeId, Guid categoryId, CancellationToken cancellationToken)
    {
        var assignee = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.Id == assigneeId && u.OrganizationId == branchContext.EffectiveOrganizationId)
            .Select(u => new { u.Role, u.CategoryId, u.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (assignee is null || assignee.Role != UserRole.Mentor || assignee.CategoryId != categoryId)
        {
            throw new ConflictException(
                ErrorCodes.CrossScopeReference,
                "Исполнитель должен быть ментором этой категории.",
                new Dictionary<string, object?> { ["relation"] = "assignedToId" });
        }

        if (!assignee.IsActive)
        {
            throw new ConflictException(
                ErrorCodes.AssigneeInactive,
                "Исполнитель деактивирован.");
        }
    }

    /// <summary>Loads the template a draft is built from, checking it belongs to the same category.</summary>
    private async Task<Domain.Schedule.TopicAssignment?> ResolveTemplateAsync(
        Guid? topicAssignmentId,
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        if (topicAssignmentId is not { } id)
        {
            return null;
        }

        var template = await dbContext.TopicAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.CategoryId == categoryId, cancellationToken);

        return template ?? throw new ConflictException(
            ErrorCodes.CrossScopeReference,
            "Шаблон задания не принадлежит этой категории.",
            new Dictionary<string, object?> { ["relation"] = "topicAssignmentId" });
    }

    /// <summary>
    /// Resolves the deadline (<c>ASN-027</c>).
    /// </summary>
    /// <remarks>
    /// A full moment is taken as given. Nothing at all falls back to the category's default number of
    /// days at its <c>DefaultDueTimeLocal</c> — which is why 2.1 introduced that field: «PlannedDate +
    /// DueDays» alone never said what time of day the work was due.
    /// </remarks>
    private async Task<DateTimeOffset> ResolveDueAtAsync(
        DateTimeOffset? requested,
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        var settings = await dbContext.CategorySettings
            .AsNoTracking()
            .Where(s => s.CategoryId == categoryId)
            .Select(s => new { s.TimeZoneId, s.DefaultAssignmentDueDays, s.DefaultDueTimeLocal })
            .FirstAsync(cancellationToken);

        var now = clock.UtcNow;

        if (requested is { } dueAt)
        {
            return dueAt > now
                ? dueAt
                : throw new ValidationAppException("dueAt", "Дедлайн должен быть в будущем.");
        }

        var localToday = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId)).DateTime);

        return deadlines.CalculateInitialDueAt(
            localToday,
            settings.DefaultAssignmentDueDays,
            settings.DefaultDueTimeLocal,
            settings.TimeZoneId);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static string ActorLabel(TaskEvent taskEvent) => taskEvent.ActorId is null ? "Система" : "Lead";

    private static IQueryable<Assignment> ApplySort(IQueryable<Assignment> source, AssignmentListQuery query)
    {
        var descending = string.Equals(query.Order, "desc", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(query.Sort)
            && !SortableColumns.Contains(query.Sort, StringComparer.OrdinalIgnoreCase))
        {
            throw new ValidationAppException(
                "sort",
                $"Сортировка допустима только по полям: {string.Join(", ", SortableColumns)}.");
        }

        return query.Sort?.ToLowerInvariant() switch
        {
            "createdat" => descending ? source.OrderByDescending(a => a.CreatedAt) : source.OrderBy(a => a.CreatedAt),
            "status" => descending ? source.OrderByDescending(a => a.Status) : source.OrderBy(a => a.Status),
            _ => descending ? source.OrderByDescending(a => a.CurrentDueAt) : source.OrderBy(a => a.CurrentDueAt),
        };
    }

    private async Task<Dictionary<Guid, BranchSummaryDto>> LoadBranchSummariesAsync(
        IEnumerable<Guid> branchIds,
        CancellationToken cancellationToken)
    {
        var ids = branchIds.Distinct().ToArray();

        return await dbContext.Branches
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => new BranchSummaryDto(b.Id, b.Name, b.Code, b.IsHeadOffice))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
    }

    private static AssignmentDto ToDto(Assignment assignment, string concurrencyToken, BranchSummaryDto? branch) => new(
        assignment.Id,
        assignment.OrganizationId,
        assignment.BranchId,
        assignment.CategoryId,
        assignment.TopicAssignmentId,
        assignment.AssignedToId,
        assignment.AssignedById,
        assignment.Title,
        assignment.Description,
        assignment.Status.ToString(),
        assignment.Source.ToString(),
        assignment.InitialDueAt,
        assignment.CurrentDueAt,
        assignment.GeneratedForDate,
        assignment.AssignedAt,
        assignment.FirstSubmittedAt,
        assignment.ReviewStartedAt,
        assignment.ApprovedAt,
        assignment.OverdueAt,
        assignment.CancelledAt,
        assignment.CancelReason,
        assignment.CreatedAt,
        concurrencyToken,
        branch);

    /// <summary>Carries the shadow <c>xmin</c> out of a no-tracking query alongside its entity.</summary>
    private sealed record AssignmentRow(Assignment Assignment, uint Xmin);
}
