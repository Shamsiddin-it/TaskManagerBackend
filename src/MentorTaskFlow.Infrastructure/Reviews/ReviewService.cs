using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Reviews;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Reviews;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MentorTaskFlow.Infrastructure.Reviews;

/// <inheritdoc />
public sealed class ReviewService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    ITenantStateGuard tenantState,
    IOutboxWriter outboxWriter,
    IRequestContext requestContext,
    IClock clock) : IReviewService
{
    public Task<ReviewDto> CreateAsync(
        Guid submissionId,
        CreateReviewRequest request,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(() => CreateCoreAsync(submissionId, request, cancellationToken));
    }

    /// <summary>
    /// <c>REV-008</c>: the decision, the transition, the deadline, the event and the notification, in
    /// one transaction.
    /// </summary>
    /// <remarks>
    /// The assignment row is locked first, as scenario 4 of Приложение K requires. Without it two
    /// decisions on one submission would both read <c>InReview</c>, both move the assignment, and the
    /// unique index would then reject one of them — after the other had already changed the deadline.
    /// </remarks>
    private async Task<ReviewDto> CreateCoreAsync(
        Guid submissionId,
        CreateReviewRequest request,
        CancellationToken cancellationToken)
    {
        var actor = RequireLead();
        var decision = ParseDecision(request.Decision);

        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var submission = await FindReviewableAsync(submissionId, actor, cancellationToken);

        await dbContext.Database.ExecuteSqlAsync(
            $"SELECT id FROM assignments WHERE id = {submission.AssignmentId} FOR UPDATE",
            cancellationToken);

        var assignment = await dbContext.Assignments
            .FirstAsync(a => a.Id == submission.AssignmentId, cancellationToken);

        dbContext.Expect(assignment, request.ConcurrencyToken);

        await tenantState.EnsureWritableAsync(assignment.BranchId, assignment.CategoryId, cancellationToken);

        // REV-004: only the latest version. A verdict on an older one would leave the work the mentor
        // actually submitted last without a decision, while the task moved on as if it had one.
        await EnsureLatestAsync(submission, cancellationToken);

        var now = clock.UtcNow;
        var previous = assignment.Status;
        var previousDueAt = assignment.CurrentDueAt;

        // Row 10 of Приложение F: an approval carrying a new deadline is 400, not a silently ignored
        // field. Dropping it would let the caller believe the deadline had been set, and Review.Approve
        // has no parameter to pass it to — so the contradiction has to be caught here, where the
        // request shape is still visible.
        if (decision is ReviewDecision.Approved && request.ReworkDueAt is not null)
        {
            throw new ValidationAppException("reworkDueAt", "При одобрении новый дедлайн не указывается.");
        }

        var review = decision is ReviewDecision.Approved
            ? Review.Approve(submission, actor.UserId, request.Comment, now)
            : Review.RequestRework(
                submission,
                actor.UserId,
                request.Comment!,
                request.ReworkDueAt ?? throw new ValidationAppException(
                    "reworkDueAt",
                    "При возврате на доработку обязателен новый дедлайн."),
                now);

        // REV-003 is enforced by the state machine itself: both methods refuse anything but InReview.
        if (decision is ReviewDecision.Approved)
        {
            assignment.Approve(now);
        }
        else
        {
            assignment.RequestRework(review.ReworkDueAt!.Value, now);
        }

        dbContext.Reviews.Add(review);

        dbContext.TaskEvents.Add(TaskEvent.Record(
            assignment,
            decision is ReviewDecision.Approved ? TaskEventType.ReviewApproved : TaskEventType.ReviewNeedsRework,
            actor.UserId,
            previous,
            assignment.Status,
            requestContext.CorrelationId,
            now,
            JsonSerializer.SerializeToDocument(new
            {
                reviewId = review.Id,
                submissionId = submission.Id,
                decision = decision.ToString(),

                // The deadline change is recorded here rather than as an event of its own: 2.1 removed
                // DueDateChanged precisely because no action produced it independently (Приложение F).
                previousCurrentDueAt = previousDueAt,
                newCurrentDueAt = assignment.CurrentDueAt,
            }),
            reviewId: review.Id,
            submissionId: submission.Id));

        await NotifyAssigneeAsync(assignment, review, cancellationToken);

        await SaveTranslatingConstraintsAsync(assignment, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ToDto(review);
    }

    public async Task<ReviewDto> GetAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        var submission = await FindVisibleSubmissionAsync(submissionId, actor, cancellationToken);

        var review = await dbContext.Reviews
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.SubmissionId == submission.Id, cancellationToken)
            ?? throw new NotFoundException("Решение по этой версии ещё не вынесено.");

        return ToDto(review);
    }

    // -----------------------------------------------------------------
    // Scope and guards
    // -----------------------------------------------------------------

    /// <summary>
    /// The submission a Lead of this very category may decide on.
    /// </summary>
    /// <remarks>
    /// A Lead of another category or another branch gets 404, not 409: the object is outside their
    /// visibility, and saying anything else would confirm it exists (<c>TEN-006</c>). The 409
    /// <c>CROSS_SCOPE_REFERENCE</c> the endpoint catalog lists is the database's answer, reached only
    /// by a path that bypasses this check.
    /// </remarks>
    private async Task<Submission> FindReviewableAsync(
        Guid submissionId,
        ICurrentUserContext actor,
        CancellationToken cancellationToken)
    {
        var submission = await dbContext.Submissions
            .FirstOrDefaultAsync(
                s => s.Id == submissionId && s.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        if (submission.CategoryId != actor.CategoryId)
        {
            throw new NotFoundException();
        }

        return submission;
    }

    private async Task EnsureLatestAsync(Submission submission, CancellationToken cancellationToken)
    {
        var latest = await dbContext.Submissions
            .AsNoTracking()
            .Where(s => s.AssignmentId == submission.AssignmentId)
            .MaxAsync(s => s.VersionNumber, cancellationToken);

        if (submission.VersionNumber != latest)
        {
            throw new ConflictException(
                ErrorCodes.ReviewNotLatestSubmission,
                "Решение выносится только по последней загруженной версии.");
        }
    }

    private async Task<Submission> FindVisibleSubmissionAsync(
        Guid submissionId,
        ICurrentUserContext actor,
        CancellationToken cancellationToken)
    {
        var submission = await dbContext.Submissions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Id == submissionId && s.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        var visible = actor switch
        {
            { Role: UserRole.Admin, AdminScope: AdminScope.Organization } => true,
            { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => submission.BranchId == actor.BranchId,
            { Role: UserRole.Lead } => submission.CategoryId == actor.CategoryId,
            _ => submission.SubmittedById == actor.UserId,
        };

        if (!visible)
        {
            throw new NotFoundException();
        }

        return submission;
    }

    /// <summary>
    /// Tells the mentor what was decided, and — on rework — by when.
    /// </summary>
    /// <remarks>
    /// The comment itself is not in the payload. Review text can be long and blunt, and email is not
    /// the place for it; the notification says a decision exists and the interface shows it
    /// (<c>NTF-005</c>).
    /// </remarks>
    private Task NotifyAssigneeAsync(Assignment assignment, Review review, CancellationToken cancellationToken) =>
        outboxWriter.EnqueueSystemAsync(
            new OutboxEntry
            {
                RecipientUserId = assignment.AssignedToId,
                EventType = review.Decision is ReviewDecision.Approved
                    ? NotificationEventTypes.ReviewApproved
                    : NotificationEventTypes.ReviewNeedsRework,
                EntityId = assignment.Id,

                // The review identifier keeps the decision on a second version from being deduplicated
                // against the decision on the first (NTF-015).
                Discriminator = review.Id.ToString("N"),
                CategoryId = assignment.CategoryId,
                Payload = JsonSerializer.SerializeToDocument(new
                {
                    assignmentTitle = assignment.Title,
                    currentDueAt = assignment.CurrentDueAt,
                }),
            },
            assignment.OrganizationId,
            assignment.BranchId,
            cancellationToken);

    /// <summary>
    /// <c>REV-005</c>: the unique index is the decision-maker, not a prior lookup.
    /// </summary>
    /// <remarks>
    /// Two Leads deciding at the same instant would both find no review and both write one. The row
    /// lock above serialises them so this is the rare path, but it stays because «rare» is not «never»
    /// (scenario 4 of Приложение K).
    /// </remarks>
    private async Task SaveTranslatingConstraintsAsync(Assignment assignment, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveWithConcurrencyCheckAsync(assignment, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                                                  {
                                                      SqlState: PostgresErrorCodes.UniqueViolation,
                                                      ConstraintName: "ux_reviews_submission",
                                                  })
        {
            throw new ConflictException(
                ErrorCodes.ReviewAlreadyExists,
                "Решение по этой версии уже вынесено.");
        }
    }

    private static ReviewDecision ParseDecision(string? value) =>
        Enum.TryParse<ReviewDecision>(value, ignoreCase: false, out var parsed)
            ? parsed
            : throw new ValidationAppException("decision", "Решение должно быть Approved или NeedsRework.");

    private ICurrentUserContext RequireActor() => currentUser.Current ?? throw new UnauthorizedException();

    /// <summary>Reviewing belongs to the Lead of the category — to no administrator (<c>ASN-025</c>).</summary>
    private ICurrentUserContext RequireLead()
    {
        var actor = RequireActor();

        return actor.Role is UserRole.Lead
            ? actor
            : throw new ForbiddenException(ErrorCodes.Forbidden, "Проверка работ доступна только тимлиду категории.");
    }

    private static ReviewDto ToDto(Review review) => new(
        review.Id,
        review.SubmissionId,
        review.AssignmentId,
        review.ReviewerId,
        review.Decision.ToString(),
        review.Comment,
        review.ReworkDueAt,
        review.CreatedAt);
}
