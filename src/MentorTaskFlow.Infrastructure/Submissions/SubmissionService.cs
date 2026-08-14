using System.Text.Json;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Submissions;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using MentorTaskFlow.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MentorTaskFlow.Infrastructure.Submissions;

/// <inheritdoc />
public sealed class SubmissionService(
    MentorTaskFlowDbContext dbContext,
    ICurrentUserAccessor currentUser,
    IBranchContext branchContext,
    ITenantStateGuard tenantState,
    IFileStorage storage,
    UploadedFileInspector inspector,
    IOutboxWriter outboxWriter,
    IRequestContext requestContext,
    IOptions<StorageOptions> storageOptions,
    IClock clock) : ISubmissionService
{
    /// <summary>Statuses from which a mentor may hand work in (<c>SUB-001</c>).</summary>
    private static readonly AssignmentStatus[] SubmittableStatuses =
    [
        AssignmentStatus.Assigned,
        AssignmentStatus.NeedsRework,
        AssignmentStatus.Overdue,
    ];

    private readonly StorageOptions _options = storageOptions.Value;

    // -----------------------------------------------------------------
    // Upload — the order of 17.2, step by step
    // -----------------------------------------------------------------

    public async Task<SubmissionDto> UploadAsync(
        Guid assignmentId,
        UploadedFile file,
        CancellationToken cancellationToken)
    {
        // Steps 0a–7: everything decidable without reading the body. Cheapest first, and none of it
        // touches storage.
        var actor = RequireMentor();
        var assignment = await FindSubmittableAsync(assignmentId, actor, cancellationToken);
        var extension = UploadedFileInspector.ResolveExtension(file.FileName, file.ContentType);

        // Steps 8 and 9: size, signature and structure. The file is spooled to a temporary handle that
        // deletes itself when the request ends, whichever way it ends.
        await using var inspected = await inspector.InspectAsync(
            file.Content,
            extension,
            file.Length,
            cancellationToken);

        // Step 10: SUB-028. A byte-identical repeat within one assignment is nearly always a second
        // click or the wrong file, and admitting it would inflate AverageVersions with work that
        // contains no change.
        await EnsureNotDuplicateAsync(assignmentId, inspected.Sha256Hash, cancellationToken);

        var submissionId = Guid.CreateVersion7();

        var storageKey = SubmissionStorageKey.For(
            assignment.OrganizationId,
            assignment.BranchId,
            assignment.CategoryId,
            assignment.Id,
            submissionId,
            extension);

        // Step 11. From here a failure leaves an orphan object rather than a row pointing at nothing —
        // the direction of that trade is deliberate (SUB-030, SUB-032).
        await storage.PutAsync(storageKey, inspected.Content, Submission.ContentTypeOf(extension), cancellationToken);

        // Step 12.
        return await PersistAsync(
            assignment,
            submissionId,
            storageKey,
            SanitiseFileName(file.FileName, extension),
            inspected,
            actor,
            cancellationToken);
    }

    /// <summary>
    /// Reduces a client-supplied name to something safe to store and to echo back.
    /// </summary>
    /// <remarks>
    /// The name never reaches the storage key (<c>SUB-009</c>), but it is shown to people and ends up
    /// inside a <c>Content-Disposition</c> header, so directory components and control characters go
    /// first. An empty result falls back to a generic name rather than failing the upload: the file
    /// itself has already passed every check that matters.
    /// </remarks>
    private static string SanitiseFileName(string? fileName, FileExtension extension)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);

        var cleaned = new string([.. name.Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/'))]).Trim();

        if (cleaned.Length == 0)
        {
            return $"submission{Submission.SuffixOf(extension)}";
        }

        return cleaned.Length > Submission.OriginalFileNameMaxLength
            ? cleaned[^Submission.OriginalFileNameMaxLength..]
            : cleaned;
    }

    /// <summary>
    /// Steps 2–6: the assignment exists, belongs to the caller, and is in a state that accepts work.
    /// </summary>
    /// <remarks>
    /// The order of the codes matters as much as the checks. An assignment outside the mentor's
    /// visibility and one that is not theirs both answer 404, byte for byte, so neither confirms the
    /// other's existence (<c>TEN-006</c>). Only once the object is known to be theirs does the answer
    /// start describing state.
    /// </remarks>
    private async Task<Assignment> FindSubmittableAsync(
        Guid assignmentId,
        ICurrentUserContext actor,
        CancellationToken cancellationToken)
    {
        var assignment = await dbContext.Assignments
            .FirstOrDefaultAsync(
                a => a.Id == assignmentId && a.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        // Step 3: ownership. A draft or a suggestion is invisible to the mentor as well, so those are
        //404 too rather than a conflict that would reveal the task exists (13.2).
        if (assignment.AssignedToId != actor.UserId
            || assignment.Status is AssignmentStatus.Draft or AssignmentStatus.Suggested)
        {
            throw new NotFoundException();
        }

        // Steps 0a, 0b and 4, in that order: the organization, then the branch, then the category.
        await tenantState.EnsureWritableAsync(assignment.BranchId, assignment.CategoryId, cancellationToken);

        // Step 5.
        if (!SubmittableStatuses.Contains(assignment.Status))
        {
            throw new ConflictException(
                ErrorCodes.SubmissionNotAllowed,
                "Загрузка работы недоступна в текущем статусе задачи.");
        }

        // Step 6: read now, not when the assignment was created, so a category that has since closed
        // late submissions actually closes them (SUB-023, CAT-016).
        if (assignment.Status is AssignmentStatus.Overdue && !await AllowsLateSubmissionAsync(assignment, cancellationToken))
        {
            throw new ConflictException(
                ErrorCodes.LateSubmissionDisabled,
                "Приём работ по этой задаче закрыт: категория не допускает поздние сдачи.");
        }

        return assignment;
    }

    private async Task<bool> AllowsLateSubmissionAsync(Assignment assignment, CancellationToken cancellationToken) =>
        await dbContext.CategorySettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.CategoryId == assignment.CategoryId)
            .Select(s => s.AllowLateSubmission)
            .FirstAsync(cancellationToken);

    private async Task EnsureNotDuplicateAsync(Guid assignmentId, string hash, CancellationToken cancellationToken)
    {
        var duplicate = await dbContext.Submissions
            .AsNoTracking()
            .AnyAsync(s => s.AssignmentId == assignmentId && s.Sha256Hash == hash, cancellationToken);

        if (duplicate)
        {
            throw new ConflictException(
                ErrorCodes.SubmissionDuplicateContent,
                "Такой файл уже загружен по этой задаче. Новая версия должна содержать изменения.");
        }
    }

    /// <summary>
    /// Step 12: the submission, the status change, the event and the notification, in one transaction.
    /// </summary>
    /// <remarks>
    /// <c>SUB-003</c> and 12.5: the version number is computed under <c>SELECT … FOR UPDATE</c> on the
    /// assignment row. Two concurrent uploads would otherwise both read the same maximum and both claim
    /// the same number; the unique index would then decide the winner after one of them had already
    /// been told it succeeded.
    /// </remarks>
    private Task<SubmissionDto> PersistAsync(
        Assignment assignment,
        Guid submissionId,
        string storageKey,
        string originalFileName,
        InspectedFile inspected,
        ICurrentUserContext actor,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            await dbContext.Database.ExecuteSqlAsync(
                $"SELECT id FROM assignments WHERE id = {assignment.Id} FOR UPDATE",
                cancellationToken);

            var tracked = await dbContext.Assignments.FirstAsync(a => a.Id == assignment.Id, cancellationToken);

            // Re-read under the lock. A cancel may have landed between the checks above and this point,
            // and scenario 9 of Приложение K requires exactly one of the two operations to take effect.
            if (!SubmittableStatuses.Contains(tracked.Status))
            {
                throw new ConflictException(
                    ErrorCodes.SubmissionNotAllowed,
                    "Загрузка работы недоступна в текущем статусе задачи.");
            }

            var version = await dbContext.Submissions
                .Where(s => s.AssignmentId == tracked.Id)
                .MaxAsync(s => (int?)s.VersionNumber, cancellationToken) ?? 0;

            var now = clock.UtcNow;

            // SUB-025: overdue by status, or simply past the deadline. The second half matters because
            // the overdue job runs every 15 minutes — work handed in three minutes late has no Overdue
            // status yet and must still be marked late.
            var isLate = tracked.Status is AssignmentStatus.Overdue || now > tracked.CurrentDueAt;

            // The identifier is the one the key was built from: the object is already in the bucket
            // under it, and a row with a different id would leave the two describing different things
            // (TEN-068).
            var submission = Submission.Record(
                submissionId,
                tracked,
                version + 1,
                storageKey,
                originalFileName,
                inspected.Extension,
                inspected.SizeBytes,
                inspected.Sha256Hash,
                isLate,
                actor.UserId,
                now);

            dbContext.Submissions.Add(submission);

            var previous = tracked.Status;
            tracked.Submit(isFirstVersion: version == 0, now);

            dbContext.TaskEvents.Add(TaskEvent.Record(
                tracked,
                isLate ? TaskEventType.LateSubmissionUploaded : TaskEventType.SubmissionUploaded,
                actor.UserId,
                previous,
                tracked.Status,
                requestContext.CorrelationId,
                now,
                JsonSerializer.SerializeToDocument(new
                {
                    submissionId,
                    versionNumber = submission.VersionNumber,
                    isLate,
                    fileSizeBytes = submission.FileSizeBytes,
                })));

            await NotifyLeadAsync(tracked, submission, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ToDto(submission);
        });
    }

    /// <summary>
    /// Tells the category's Lead that work is waiting, and whether it arrived late.
    /// </summary>
    /// <remarks>
    /// Addressed to the active Lead of the assignment's own category — not the mentor's current one.
    /// After a transfer those differ, and the person who owns the task is the one who has to review it
    /// (10.6.4).
    /// </remarks>
    private async Task NotifyLeadAsync(Assignment assignment, Submission submission, CancellationToken cancellationToken)
    {
        var leadId = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.CategoryId == assignment.CategoryId && u.Role == UserRole.Lead && u.IsActive)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (leadId is not { } recipient)
        {
            // A category without an active Lead is a valid, observable state — deactivating the last
            // one is allowed and raises its own alert (USER-005). Losing this notification is the
            // consequence, and it is preferable to failing the upload.
            return;
        }

        outboxWriter.EnqueueSystem(
            new OutboxEntry
            {
                RecipientUserId = recipient,
                EventType = submission.IsLate
                    ? NotificationEventTypes.LateSubmissionUploaded
                    : NotificationEventTypes.SubmissionUploaded,
                Channel = NotificationChannel.Email,
                DeduplicationKey = $"submission:{submission.Id:N}",
                CategoryId = assignment.CategoryId,
                Payload = JsonSerializer.SerializeToDocument(new
                {
                    assignmentTitle = assignment.Title,
                    versionNumber = submission.VersionNumber,
                    isLate = submission.IsLate,
                }),
            },
            assignment.OrganizationId,
            assignment.BranchId);
    }

    // -----------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------

    public async Task<IReadOnlyList<SubmissionDto>> ListAsync(Guid assignmentId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();
        await EnsureAssignmentVisibleAsync(assignmentId, actor, cancellationToken);

        var submissions = await dbContext.Submissions
            .AsNoTracking()
            .Where(s => s.AssignmentId == assignmentId)
            .OrderByDescending(s => s.VersionNumber)
            .ToListAsync(cancellationToken);

        return submissions.Select(ToDto).ToList();
    }

    public async Task<FileUrlDto> GetDownloadUrlAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        var submission = await FindDownloadableAsync(submissionId, cancellationToken);

        var url = await storage.GetDownloadUrlAsync(
            submission.StorageKey,
            submission.ContentType,
            submission.OriginalFileName,
            cancellationToken);

        return Expiring(url);
    }

    public async Task<FileUrlDto> GetPreviewUrlAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        var submission = await FindDownloadableAsync(submissionId, cancellationToken);

        // 17.5: a PPTX has no preview in Release 1.0. 404 rather than a conflict — the resource does
        // not exist, and saying so is not a refusal.
        if (submission.PreviewStorageKey is not { } previewKey)
        {
            throw new NotFoundException("Предпросмотр доступен только для PDF.");
        }

        var url = await storage.GetPreviewUrlAsync(previewKey, submission.ContentType, cancellationToken);

        return Expiring(url);
    }

    private FileUrlDto Expiring(Uri url) =>
        new(url.ToString(), clock.UtcNow.AddMinutes(_options.PresignedUrlMinutes));

    /// <summary>
    /// Repeats levels 1–5 before a URL is issued (<c>SEC-004</c>, <c>TEN-064</c>).
    /// </summary>
    /// <remarks>
    /// The check that is easy to miss is <c>TEN-065</c>: an Organization Admin in the all-branches mode
    /// is refused with 400 rather than served. Handing out a file is an operation of one branch, and
    /// the aggregate mode has nothing to attribute it to.
    /// </remarks>
    private async Task<Submission> FindDownloadableAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        var actor = RequireActor();

        if (actor is { Role: UserRole.Admin, AdminScope: AdminScope.Organization }
            && branchContext.IsAllBranchesReadContext)
        {
            throw new BranchContextRequiredException();
        }

        var submission = await dbContext.Submissions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Id == submissionId && s.OrganizationId == branchContext.EffectiveOrganizationId,
                cancellationToken)
            ?? throw new NotFoundException();

        EnsureVisible(actor, submission);

        return submission;
    }

    private async Task EnsureAssignmentVisibleAsync(
        Guid assignmentId,
        ICurrentUserContext actor,
        CancellationToken cancellationToken)
    {
        var assignment = await dbContext.Assignments
            .AsNoTracking()
            .Where(a => a.Id == assignmentId && a.OrganizationId == branchContext.EffectiveOrganizationId)
            .Select(a => new { a.AssignedToId, a.CategoryId, a.BranchId, a.Status })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        var visible = actor switch
        {
            { Role: UserRole.Admin, AdminScope: AdminScope.Organization } => true,
            { Role: UserRole.Admin, AdminScope: AdminScope.Branch } => assignment.BranchId == actor.BranchId,
            { Role: UserRole.Lead } => assignment.CategoryId == actor.CategoryId,

            _ => assignment.AssignedToId == actor.UserId
                 && assignment.Status is not (AssignmentStatus.Draft or AssignmentStatus.Suggested),
        };

        if (!visible)
        {
            throw new NotFoundException();
        }
    }

    private static void EnsureVisible(ICurrentUserContext actor, Submission submission)
    {
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
    }

    private ICurrentUserContext RequireActor() => currentUser.Current ?? throw new UnauthorizedException();

    private ICurrentUserContext RequireMentor()
    {
        var actor = RequireActor();

        return actor.Role is UserRole.Mentor
            ? actor
            : throw new ForbiddenException(ErrorCodes.Forbidden, "Загрузка работы доступна только исполнителю задачи.");
    }

    private static SubmissionDto ToDto(Submission submission) => new(
        submission.Id,
        submission.AssignmentId,
        submission.VersionNumber,
        submission.OriginalFileName,
        submission.ContentType,
        submission.FileExtension.ToString(),
        submission.FileSizeBytes,
        submission.Sha256Hash,
        submission.IsLate,
        submission.SubmittedById,
        submission.SubmittedAt,
        submission.PreviewStorageKey is not null);
}
