using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Schedule;
using MentorTaskFlow.Contracts.Tenancy;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Schedule;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Persistence;
using MentorTaskFlow.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MentorTaskFlow.Infrastructure.Schedule;

/// <inheritdoc />
public sealed class ScheduleService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    ITenantStateGuard tenantState,
    IAuditWriter auditWriter,
    IClock clock) : IScheduleService
{
    private static readonly string[] SortableColumns = ["dayNumber", "plannedDate", "title"];

    // -----------------------------------------------------------------
    // Topics
    // -----------------------------------------------------------------

    public async Task<PagedResult<TopicDto>> ListTopicsAsync(TopicListQuery query, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        var page = Math.Max(query.Page, PaginationLimits.DefaultPage);
        var pageSize = Math.Clamp(query.PageSize, PaginationLimits.MinPageSize, PaginationLimits.MaxPageSize);

        var source = ApplyVisibility(
            dbContext.Topics.AsNoTracking().Where(t => t.OrganizationId == branchContext.EffectiveOrganizationId),
            actor);

        if (query.CategoryId is { } categoryId)
        {
            source = source.Where(t => t.CategoryId == categoryId);
        }

        if (query.IsActive is { } isActive)
        {
            source = source.Where(t => t.IsActive == isActive);
        }

        source = ApplySort(source, query);

        var totalCount = await source.CountAsync(cancellationToken);

        var rows = await source
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TopicRow(t, EF.Property<uint>(t, ConcurrencyTokenExtensions.PropertyName)))
            .ToListAsync(cancellationToken);

        var branches = branchContext.IsAllBranchesReadContext
            ? await LoadBranchSummariesAsync(rows.Select(r => r.Topic.BranchId), cancellationToken)
            : [];

        var items = rows
            .Select(row => ToDto(
                row.Topic,
                ConcurrencyTokenAccessor.EncodeFrom(row.Xmin),
                branches.GetValueOrDefault(row.Topic.BranchId)))
            .ToList();

        return new PagedResult<TopicDto>(items, page, pageSize, totalCount);
    }

    public async Task<TopicDto> GetTopicAsync(Guid topicId, CancellationToken cancellationToken)
    {
        var row = await FindReadableTopicAsync(topicId, cancellationToken);

        return ToDto(row.Topic, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin), null);
    }

    public async Task<TopicDto> CreateTopicAsync(CreateTopicRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        EnsureMayWriteSchedule(actor);

        // The category decides the scope. Its own branch is used rather than the request context, so
        // a topic can never land in a category that lives somewhere else (TPL-001, SEC-003).
        var category = await FindWritableCategoryAsync(actor, request.CategoryId, cancellationToken);

        var topic = Topic.Create(
            category.OrganizationId,
            category.BranchId,
            category.Id,
            request.DayNumber,
            request.PlannedDate,
            request.Title,
            request.Description,
            clock.UtcNow);

        dbContext.Topics.Add(topic);

        auditWriter.Write(TopicAudit(AuditActions.TopicCreate, topic));

        await SaveTranslatingConstraintsAsync(cancellationToken);

        return ToDto(topic, dbContext.Read(topic), null);
    }

    public async Task<TopicDto> UpdateTopicAsync(Guid topicId, UpdateTopicRequest request, CancellationToken cancellationToken)
    {
        var topic = await FindWritableTopicAsync(topicId, cancellationToken);
        dbContext.Expect(topic, request.ConcurrencyToken);

        // TOPIC-005: moving PlannedDate does not shift the deadlines of assignments already created —
        // those are absolute UTC moments.
        topic.Update(request.DayNumber, request.PlannedDate, request.Title, request.Description, clock.UtcNow);

        auditWriter.Write(TopicAudit(AuditActions.TopicUpdate, topic));

        await SaveTranslatingConstraintsAsync(cancellationToken, topic);

        return ToDto(topic, dbContext.Read(topic), null);
    }

    public async Task<TopicDto> ActivateTopicAsync(Guid topicId, ScheduleActionRequest request, CancellationToken cancellationToken)
    {
        var topic = await FindWritableTopicAsync(topicId, cancellationToken);
        dbContext.Expect(topic, request.ConcurrencyToken);

        topic.Activate(clock.UtcNow);
        auditWriter.Write(TopicAudit(AuditActions.TopicUpdate, topic));

        // Reactivating can collide with another active topic on the same planned date, which the
        // partial unique index refuses (TOPIC-010).
        await SaveTranslatingConstraintsAsync(cancellationToken, topic);

        return ToDto(topic, dbContext.Read(topic), null);
    }

    public async Task<TopicDto> DeactivateTopicAsync(Guid topicId, ScheduleActionRequest request, CancellationToken cancellationToken)
    {
        var topic = await FindWritableTopicAsync(topicId, cancellationToken);
        dbContext.Expect(topic, request.ConcurrencyToken);

        topic.Deactivate(clock.UtcNow);
        auditWriter.Write(TopicAudit(AuditActions.TopicUpdate, topic));

        await dbContext.SaveWithConcurrencyCheckAsync(topic, cancellationToken);

        return ToDto(topic, dbContext.Read(topic), null);
    }

    public async Task DeleteTopicAsync(Guid topicId, CancellationToken cancellationToken)
    {
        var topic = await FindWritableTopicAsync(topicId, cancellationToken);

        // TOPIC-003: a topic with templates is refused before the database has to. The FK would refuse
        // it anyway, but a checked answer names the reason rather than surfacing a constraint name.
        var hasTemplates = await dbContext.TopicAssignments
            .AnyAsync(a => a.TopicId == topic.Id, cancellationToken);

        if (hasTemplates)
        {
            throw new ConflictException(
                ErrorCodes.ResourceInUse,
                "Тема содержит задания. Удалите их или деактивируйте тему.");
        }

        dbContext.Topics.Remove(topic);
        auditWriter.Write(TopicAudit(AuditActions.TopicDelete, topic));

        await SaveTranslatingConstraintsAsync(cancellationToken);
    }

    // -----------------------------------------------------------------
    // Topic assignments
    // -----------------------------------------------------------------

    public async Task<IReadOnlyList<TopicAssignmentDto>> ListTopicAssignmentsAsync(Guid topicId, CancellationToken cancellationToken)
    {
        // Reuses the topic read check, so a topic outside the caller's reach answers 404 rather than
        // an empty list that would confirm it exists.
        await FindReadableTopicAsync(topicId, cancellationToken);

        var rows = await dbContext.TopicAssignments
            .AsNoTracking()
            .Where(a => a.TopicId == topicId)
            .OrderBy(a => a.Type)
            .ThenBy(a => a.Title)
            .Select(a => new TemplateRow(a, EF.Property<uint>(a, ConcurrencyTokenExtensions.PropertyName)))
            .ToListAsync(cancellationToken);

        return rows.Select(row => ToDto(row.Template, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin))).ToList();
    }

    public async Task<TopicAssignmentDto> GetTopicAssignmentAsync(Guid topicAssignmentId, CancellationToken cancellationToken)
    {
        var row = await dbContext.TopicAssignments
            .AsNoTracking()
            .Where(a => a.Id == topicAssignmentId && a.OrganizationId == branchContext.EffectiveOrganizationId)
            .Select(a => new TemplateRow(a, EF.Property<uint>(a, ConcurrencyTokenExtensions.PropertyName)))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        EnsureTemplateVisible(RequireActor(), row.Template);

        return ToDto(row.Template, ConcurrencyTokenAccessor.EncodeFrom(row.Xmin));
    }

    public async Task<TopicAssignmentDto> CreateTopicAssignmentAsync(
        Guid topicId,
        CreateTopicAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        var topic = await FindWritableTopicAsync(topicId, cancellationToken);

        var template = TopicAssignment.Create(
            topic,
            ParseType(request.Type),
            request.Title,
            request.Description,
            request.IsRequired,
            clock.UtcNow);

        dbContext.TopicAssignments.Add(template);
        auditWriter.Write(TemplateAudit(AuditActions.TopicAssignmentCreate, template));

        await SaveTranslatingConstraintsAsync(cancellationToken);

        return ToDto(template, dbContext.Read(template));
    }

    public async Task<TopicAssignmentDto> UpdateTopicAssignmentAsync(
        Guid topicAssignmentId,
        UpdateTopicAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        var template = await FindWritableTemplateAsync(topicAssignmentId, cancellationToken);
        dbContext.Expect(template, request.ConcurrencyToken);

        // TPL-004: assignments already created keep the title and description copied at their
        // creation; this edit reaches only future ones.
        template.Update(ParseType(request.Type), request.Title, request.Description, request.IsRequired, clock.UtcNow);

        auditWriter.Write(TemplateAudit(AuditActions.TopicAssignmentUpdate, template));

        await dbContext.SaveWithConcurrencyCheckAsync(template, cancellationToken);

        return ToDto(template, dbContext.Read(template));
    }

    public async Task<TopicAssignmentDto> ActivateTopicAssignmentAsync(
        Guid topicAssignmentId,
        ScheduleActionRequest request,
        CancellationToken cancellationToken) =>
        await SetTemplateActiveAsync(topicAssignmentId, request, isActive: true, cancellationToken);

    public async Task<TopicAssignmentDto> DeactivateTopicAssignmentAsync(
        Guid topicAssignmentId,
        ScheduleActionRequest request,
        CancellationToken cancellationToken) =>
        await SetTemplateActiveAsync(topicAssignmentId, request, isActive: false, cancellationToken);

    public async Task DeleteTopicAssignmentAsync(Guid topicAssignmentId, CancellationToken cancellationToken)
    {
        var template = await FindWritableTemplateAsync(topicAssignmentId, cancellationToken);

        dbContext.TopicAssignments.Remove(template);
        auditWriter.Write(TemplateAudit(AuditActions.TopicAssignmentDelete, template));

        // TPL-002: once assignments reference the template, fk_assignments_template_scope refuses the
        // delete and the violation is translated below. That table arrives with the assignment
        // lifecycle; until then nothing can reference a template and the check is inert.
        await SaveTranslatingConstraintsAsync(cancellationToken);
    }

    private async Task<TopicAssignmentDto> SetTemplateActiveAsync(
        Guid topicAssignmentId,
        ScheduleActionRequest request,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var template = await FindWritableTemplateAsync(topicAssignmentId, cancellationToken);
        dbContext.Expect(template, request.ConcurrencyToken);

        var now = clock.UtcNow;

        if (isActive)
        {
            template.Activate(now);
        }
        else
        {
            template.Deactivate(now);
        }

        auditWriter.Write(TemplateAudit(AuditActions.TopicAssignmentUpdate, template));

        await dbContext.SaveWithConcurrencyCheckAsync(template, cancellationToken);

        return ToDto(template, dbContext.Read(template));
    }

    // -----------------------------------------------------------------
    // Visibility and scope
    // -----------------------------------------------------------------

    private ICurrentUserContext RequireActor() => currentUser.Current ?? throw new UnauthorizedException();

    /// <summary>
    /// <c>TOPIC-004</c>: a Mentor reads the schedule and never changes it.
    /// </summary>
    /// <remarks>
    /// The plan is the Lead's instrument; a mentor who could edit it could hand themselves different
    /// work from the one the curriculum prescribes.
    /// </remarks>
    private static void EnsureMayWriteSchedule(ICurrentUserContext actor)
    {
        if (actor.Role is UserRole.Mentor)
        {
            throw new ForbiddenException(ErrorCodes.Forbidden, "Расписание доступно ментору только для чтения.");
        }
    }

    private static IQueryable<Topic> ApplyVisibility(IQueryable<Topic> source, ICurrentUserContext actor) => actor switch
    {
        { Role: UserRole.Lead or UserRole.Mentor } => source.Where(t => t.CategoryId == actor.CategoryId),
        { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => source.Where(t => t.BranchId == actor.BranchId),
        _ => source,
    };

    private async Task<TopicRow> FindReadableTopicAsync(Guid topicId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        var row = await ApplyVisibility(
                dbContext.Topics
                    .AsNoTracking()
                    .Where(t => t.Id == topicId && t.OrganizationId == branchContext.EffectiveOrganizationId),
                actor)
            .Select(t => new TopicRow(t, EF.Property<uint>(t, ConcurrencyTokenExtensions.PropertyName)))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        return row;
    }

    /// <summary>Loads a tracked topic the caller may edit, with the whole chain above it active.</summary>
    private async Task<Topic> FindWritableTopicAsync(Guid topicId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        EnsureMayWriteSchedule(actor);

        var topic = await dbContext.Topics.FirstOrDefaultAsync(
                t => t.Id == topicId && t.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        // 404 rather than 403 throughout: a topic of another branch or another category must be
        // indistinguishable from one that does not exist (TEN-006).
        var visible = actor switch
        {
            { Role: UserRole.Lead } => topic.CategoryId == actor.CategoryId,
            { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => topic.BranchId == actor.BranchId,
            _ => true,
        };

        if (!visible)
        {
            throw new NotFoundException();
        }

        await tenantState.EnsureWritableAsync(topic.BranchId, topic.CategoryId, cancellationToken);

        return topic;
    }

    private async Task<TopicAssignment> FindWritableTemplateAsync(Guid templateId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        EnsureMayWriteSchedule(actor);

        var template = await dbContext.TopicAssignments.FirstOrDefaultAsync(
                a => a.Id == templateId && a.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        EnsureTemplateVisible(actor, template);

        await tenantState.EnsureWritableAsync(template.BranchId, template.CategoryId, cancellationToken);

        return template;
    }

    private static void EnsureTemplateVisible(ICurrentUserContext actor, TopicAssignment template)
    {
        var visible = actor switch
        {
            { Role: UserRole.Lead or UserRole.Mentor } => template.CategoryId == actor.CategoryId,
            { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => template.BranchId == actor.BranchId,
            _ => true,
        };

        if (!visible)
        {
            throw new NotFoundException();
        }
    }

    /// <summary>Loads the category a new topic will belong to, checking reach and activity.</summary>
    private async Task<Domain.Categories.Category> FindWritableCategoryAsync(
        ICurrentUserContext actor,
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        var category = await dbContext.Categories
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Id == categoryId && c.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        var reachable = actor switch
        {
            // A Lead works in exactly one category and cannot plan another's curriculum.
            { Role: UserRole.Lead } => category.Id == actor.CategoryId,
            { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => category.BranchId == actor.BranchId,

            // An Organization Admin must have chosen a branch, and the category has to be in it.
            { Role: UserRole.Admin, AdminScope: AdminScope.Organization } =>
                category.BranchId == branchContext.RequireBranchForMutation(),

            _ => false,
        };

        if (!reachable)
        {
            throw new NotFoundException();
        }

        await tenantState.EnsureWritableAsync(category.BranchId, category.Id, cancellationToken);

        return category;
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static TopicAssignmentType ParseType(string value) =>
        Enum.TryParse<TopicAssignmentType>(value, ignoreCase: false, out var parsed)
            ? parsed
            : throw new ValidationAppException(
                "type",
                "Тип задания должен быть одним из: Presentation, ClassTask, HomeTask.");

    private static IQueryable<Topic> ApplySort(IQueryable<Topic> source, TopicListQuery query)
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
            "planneddate" => descending ? source.OrderByDescending(t => t.PlannedDate) : source.OrderBy(t => t.PlannedDate),
            "title" => descending ? source.OrderByDescending(t => t.Title) : source.OrderBy(t => t.Title),
            _ => descending ? source.OrderByDescending(t => t.DayNumber) : source.OrderBy(t => t.DayNumber),
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

    private static AuditEntry TopicAudit(string action, Topic topic) => new()
    {
        Action = action,
        EntityType = nameof(Topic),
        EntityId = topic.Id,
        BranchId = topic.BranchId,
        CategoryId = topic.CategoryId,
        Metadata = JsonSerializer.SerializeToDocument(new { dayNumber = topic.DayNumber, title = topic.Title }),
    };

    private static AuditEntry TemplateAudit(string action, TopicAssignment template) => new()
    {
        Action = action,
        EntityType = nameof(TopicAssignment),
        EntityId = template.Id,
        BranchId = template.BranchId,
        CategoryId = template.CategoryId,
        Metadata = JsonSerializer.SerializeToDocument(new { type = template.Type.ToString(), title = template.Title }),
    };

    /// <summary>
    /// Maps the schedule's constraint violations onto their catalog codes.
    /// </summary>
    /// <remarks>
    /// The unique indexes are the deciders rather than a fallback: two concurrent creations would both
    /// pass a prior lookup and one still has to lose (<c>TOPIC-001</c>, <c>TOPIC-002</c>).
    /// </remarks>
    private async Task SaveTranslatingConstraintsAsync<TEntity>(CancellationToken cancellationToken, TEntity tracked)
        where TEntity : class
    {
        try
        {
            await dbContext.SaveWithConcurrencyCheckAsync(tracked, cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw Translate(exception);
        }
    }

    private async Task SaveTranslatingConstraintsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw Translate(exception);
        }
    }

    private static Exception Translate(DbUpdateException exception) =>
        exception.InnerException switch
        {
            PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "ux_topics_category_day",
            } => new ConflictException(
                ErrorCodes.ResourceAlreadyExists,
                "В категории уже есть тема с таким номером дня."),

            PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "ux_topics_category_planned_date",
            } => new ConflictException(
                ErrorCodes.ResourceAlreadyExists,
                "В категории уже есть активная тема на эту дату."),

            // Something still references the row. The FK is what makes TOPIC-003 and TPL-002 hold even
            // when the application check is bypassed.
            PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } => new ConflictException(
                ErrorCodes.ResourceInUse,
                "Объект используется и не может быть удалён. Используйте деактивацию."),

            _ => exception,
        };

    private static TopicDto ToDto(Topic topic, string concurrencyToken, BranchSummaryDto? branch) => new(
        topic.Id,
        topic.OrganizationId,
        topic.BranchId,
        topic.CategoryId,
        topic.DayNumber,
        topic.PlannedDate,
        topic.Title,
        topic.Description,
        topic.IsActive,
        topic.CreatedAt,
        topic.UpdatedAt,
        concurrencyToken,
        branch);

    private static TopicAssignmentDto ToDto(TopicAssignment template, string concurrencyToken) => new(
        template.Id,
        template.TopicId,
        template.OrganizationId,
        template.BranchId,
        template.CategoryId,
        template.Type.ToString(),
        template.Title,
        template.Description,
        template.IsRequired,
        template.IsActive,
        template.CreatedAt,
        template.UpdatedAt,
        concurrencyToken);

    /// <summary>Carries the shadow <c>xmin</c> out of a no-tracking query alongside its entity.</summary>
    private sealed record TopicRow(Topic Topic, uint Xmin);

    private sealed record TemplateRow(TopicAssignment Template, uint Xmin);
}
