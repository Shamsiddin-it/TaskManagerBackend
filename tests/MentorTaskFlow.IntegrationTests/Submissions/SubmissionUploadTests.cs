using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MentorTaskFlow.Contracts.Assignments;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Submissions;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.IntegrationTests.Submissions;

/// <summary>Upload, validation and file access (TZ 15.6, 17).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SubmissionUploadTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _organizationId;
    private Guid _headOfficeId;
    private Guid _sharpId;
    private Guid _mentorId;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        await minio.ResetAsync();
        await SeedAsync();

        _factory = new MentorTaskFlowApiFactory
        {
            ConnectionStringOverride = postgres.ConnectionString,
            StorageEndpointOverride = minio.Endpoint,
        };
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------
    // The happy path and what it writes
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_mentor_uploads_a_pdf_and_the_task_moves_to_submitted()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var response = await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var submission = await ReadAsync<SubmissionDto>(response);
        submission.VersionNumber.ShouldBe(1);
        submission.FileExtension.ShouldBe(nameof(FileExtension.Pdf));
        submission.IsLate.ShouldBeFalse();
        submission.HasPreview.ShouldBeTrue();
        submission.SubmittedById.ShouldBe(_mentorId);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        (await context.Assignments.SingleAsync(a => a.Id == assignmentId)).Status
            .ShouldBe(AssignmentStatus.Submitted);
    }

    /// <summary>
    /// <c>SUB-009</c> and <c>TEST-TEN-017</c>: the key carries the full scope, and every segment comes
    /// from the assignment on the server rather than from anything the client sent.
    /// </summary>
    [Fact]
    public async Task The_storage_key_carries_the_scope_of_the_assignment()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var submission = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf"));

        var key = (await minio.ListKeysAsync()).ShouldHaveSingleItem();

        key.ShouldBe(
            $"submissions/{_organizationId}/{_headOfficeId}/{_sharpId}/{assignmentId}/{submission.Id}.pdf");

        // SUB-009: the name the person chose is not part of the path — that is what removes traversal
        // and collisions at once.
        key.ShouldNotContain("работа");
    }

    /// <summary><c>SUB-008</c>: the key is a server-side detail and never appears in a response.</summary>
    [Fact]
    public async Task The_storage_key_is_never_returned_to_a_client()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var created = await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf");

        (await created.Content.ReadAsStringAsync()).ShouldNotContain("submissions/");

        var listed = await mentor.GetAsync($"/api/v1/assignments/{assignmentId}/submissions");
        (await listed.Content.ReadAsStringAsync()).ShouldNotContain("submissions/");
    }

    [Fact]
    public async Task An_upload_notifies_the_lead_and_records_an_event()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf");

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var notification = await context.NotificationOutbox
            .SingleAsync(n => n.EventType == NotificationEventTypes.SubmissionUploaded);

        notification.UserId.ShouldBe(await LeadIdAsync());

        var events = await context.TaskEvents
            .Where(e => e.AssignmentId == assignmentId)
            .OrderBy(e => e.SequenceNumber)
            .Select(e => e.EventType)
            .ToListAsync();

        events.ShouldBe([
            TaskEventType.DraftCreated,
            TaskEventType.Assigned,
            TaskEventType.SubmissionUploaded,
        ]);
    }

    [Fact]
    public async Task A_second_upload_becomes_version_two()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        await UploadAsync(mentor, assignmentId, Pdf(), "первая.pdf");

        await ReturnForReworkAsync(assignmentId);

        var second = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, assignmentId, Pdf("вторая версия"), "вторая.pdf"));

        second.VersionNumber.ShouldBe(2);

        var listed = await ReadAsync<List<SubmissionDto>>(
            await mentor.GetAsync($"/api/v1/assignments/{assignmentId}/submissions"));

        listed.Select(s => s.VersionNumber).ShouldBe([2, 1]);
    }

    // -----------------------------------------------------------------
    // Who may upload, and when (steps 1–6)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Only_the_assignee_may_upload()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var other = await SignInAsync("mentor2-head@mentortaskflow.test");

        // 404, not 403: an assignment that is not theirs is indistinguishable from one that does not
        // exist (TEN-006).
        (await UploadAsync(other, assignmentId, Pdf(), "чужая.pdf"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("lead-sharp@mentortaskflow.test")]
    [InlineData("branch-admin-head@mentortaskflow.test")]
    public async Task Nobody_but_a_mentor_may_upload(string email)
    {
        var assignmentId = await PublishAssignmentAsync();

        using var client = await SignInAsync(email);

        (await UploadAsync(client, assignmentId, Pdf(), "работа.pdf"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary><c>SUB-001</c>: a submitted task is with the Lead; a second upload is not a new version.</summary>
    [Fact]
    public async Task A_task_awaiting_review_does_not_accept_more_work()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        await UploadAsync(mentor, assignmentId, Pdf(), "первая.pdf");

        var response = await UploadAsync(mentor, assignmentId, Pdf("другое"), "вторая.pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.SubmissionNotAllowed);
    }

    /// <summary>
    /// <c>SUB-002</c> and 13.5: with late submission closed, the file is refused <b>and never stored</b>.
    /// </summary>
    [Fact]
    public async Task A_closed_category_refuses_a_late_upload_without_storing_it()
    {
        var assignmentId = await PublishAssignmentAsync();
        await MarkOverdueAsync(assignmentId);
        await SetAllowLateSubmissionAsync(false);

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var response = await UploadAsync(mentor, assignmentId, Pdf(), "поздняя.pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.LateSubmissionDisabled);

        (await minio.ListKeysAsync()).ShouldBeEmpty();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        (await context.Assignments.SingleAsync(a => a.Id == assignmentId)).Status
            .ShouldBe(AssignmentStatus.Overdue);
    }

    /// <summary><c>SUB-025</c>: overdue work is accepted when the category allows it, and marked late.</summary>
    [Fact]
    public async Task An_overdue_upload_is_marked_late()
    {
        var assignmentId = await PublishAssignmentAsync();
        await MarkOverdueAsync(assignmentId);

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var submission = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, assignmentId, Pdf(), "поздняя.pdf"));

        submission.IsLate.ShouldBeTrue();

        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        (await context.TaskEvents.AnyAsync(e => e.EventType == TaskEventType.LateSubmissionUploaded))
            .ShouldBeTrue();
    }

    /// <summary>
    /// The second half of <c>SUB-025</c>: past the deadline but not yet marked overdue, because the job
    /// runs every fifteen minutes. Still late.
    /// </summary>
    [Fact]
    public async Task Work_handed_in_after_the_deadline_is_late_even_before_the_job_runs()
    {
        var assignmentId = await PublishAssignmentAsync();
        await MoveDeadlineIntoThePastAsync(assignmentId);

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var submission = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf"));

        submission.IsLate.ShouldBeTrue();
    }

    // -----------------------------------------------------------------
    // File validation (steps 7–10)
    // -----------------------------------------------------------------

    [Fact]
    public async Task An_unsupported_extension_is_refused()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var response = await UploadAsync(mentor, assignmentId, "любой текст"u8.ToArray(), "работа.txt");

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.FileTypeNotAllowed);
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var response = await UploadAsync(mentor, assignmentId, [], "пустая.pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.FileEmpty);
    }

    /// <summary><c>SUB-016</c>: the signature, and the trailer, and nothing parsed in between.</summary>
    [Theory]
    [InlineData("не pdf вовсе, но с расширением", "подделка.pdf")]
    public async Task A_file_that_is_not_a_pdf_is_refused(string content, string fileName)
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var response = await UploadAsync(mentor, assignmentId, Encoding.UTF8.GetBytes(content), fileName);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.FileSignatureMismatch);
    }

    [Fact]
    public async Task A_pdf_without_a_trailer_is_refused()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        var truncated = Encoding.ASCII.GetBytes("%PDF-1.7\nСодержимое без завершающего маркера");
        var response = await UploadAsync(mentor, assignmentId, truncated, "обрезанная.pdf");

        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.FileSignatureMismatch);
    }

    /// <summary>
    /// <c>SUB-017</c>: the check that magic bytes alone cannot make. A renamed ZIP carries
    /// <c>PK\x03\x04</c> exactly like a presentation does.
    /// </summary>
    [Fact]
    public async Task A_renamed_zip_is_not_a_presentation()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var response = await UploadAsync(mentor, assignmentId, PlainZip(), "переименованный.pptx");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.PptxStructureInvalid);
    }

    [Fact]
    public async Task A_presentation_missing_its_declared_content_type_is_refused()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        var response = await UploadAsync(
            mentor,
            assignmentId,
            Pptx(declaresPresentationType: false),
            "неполная.pptx");

        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.PptxStructureInvalid);
    }

    [Fact]
    public async Task A_valid_presentation_is_accepted_and_has_no_preview()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var submission = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, assignmentId, Pptx(), "презентация.pptx"));

        submission.FileExtension.ShouldBe(nameof(FileExtension.Pptx));

        // 17.5: PPTX has no preview in Release 1.0, so PreviewStorageKey is null and the flag says so.
        submission.HasPreview.ShouldBeFalse();
    }

    /// <summary><c>SUB-018</c>: an entry name that could escape its directory is refused on sight.</summary>
    [Fact]
    public async Task An_archive_with_a_traversing_entry_name_is_refused()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var response = await UploadAsync(mentor, assignmentId, Pptx(traversalEntry: true), "обход.pptx");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ZipSafetyLimitExceeded);
    }

    /// <summary>
    /// <c>SUB-018</c>: a small archive expanding to hundreds of megabytes. Nothing is extracted — the
    /// ratio alone settles it.
    /// </summary>
    [Fact]
    public async Task A_zip_bomb_is_refused()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var response = await UploadAsync(mentor, assignmentId, ZipBomb(), "бомба.pptx");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ZipSafetyLimitExceeded);

        (await minio.ListKeysAsync()).ShouldBeEmpty();
    }

    /// <summary><c>SUB-028</c>: a byte-identical repeat within one assignment contains no change.</summary>
    [Fact]
    public async Task The_same_file_twice_on_one_assignment_is_refused()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf");
        await ReturnForReworkAsync(assignmentId);

        var response = await UploadAsync(mentor, assignmentId, Pdf(), "работа-снова.pdf");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.SubmissionDuplicateContent);

        // Refused at step 10, before storage: only the first version's object exists.
        (await minio.ListKeysAsync()).Count.ShouldBe(1);
    }

    /// <summary>
    /// <c>SUB-029</c>: the same file under a different assignment is legitimate — a shared template, for
    /// one — and the index over the hash is deliberately not unique.
    /// </summary>
    [Fact]
    public async Task The_same_file_on_a_different_assignment_is_allowed()
    {
        var first = await PublishAssignmentAsync();
        var second = await PublishAssignmentAsync("Вторая задача");

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        (await UploadAsync(mentor, first, Pdf(), "работа.pdf")).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await UploadAsync(mentor, second, Pdf(), "работа.pdf")).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary><c>TEN-061</c>: scope in the form is refused rather than quietly ignored.</summary>
    [Fact]
    public async Task Scope_supplied_in_the_form_is_refused()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Pdf());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "работа.pdf");
        form.Add(new StringContent(Guid.CreateVersion7().ToString()), "branchId");

        var response = await mentor.PostAsync($"/api/v1/assignments/{assignmentId}/submissions", form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    // -----------------------------------------------------------------
    // File access (SUB-006, TEN-065)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_download_url_is_issued_and_never_cached()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var submission = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf"));

        var response = await mentor.GetAsync($"/api/v1/submissions/{submission.Id}/download-url");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        var url = await ReadAsync<FileUrlDto>(response);
        url.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        url.Url.ShouldStartWith(minio.Endpoint);

        // The URL is what a browser follows: it must actually serve the bytes that were uploaded.
        using var anonymous = new HttpClient();
        var file = await anonymous.GetAsync(url.Url);

        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await file.Content.ReadAsByteArrayAsync()).ShouldBe(Pdf());
        file.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
    }

    /// <summary><c>SEC-013</c>: the bucket denies anonymous reads, so the presigned URL is the only way in.</summary>
    [Fact]
    public async Task The_object_is_unreachable_without_a_signature()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf");

        var key = (await minio.ListKeysAsync()).Single();

        using var anonymous = new HttpClient();

        // Built through Uri so the endpoint's trailing slash cannot produce a double one — MinIO reads
        // that as an empty bucket name and answers 400, which would pass a laxer assertion while
        // proving nothing about the policy.
        var direct = new Uri(new Uri(minio.Endpoint), $"{MinioFixture.Bucket}/{key}");

        var response = await anonymous.GetAsync(direct);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    /// <summary><c>SEC-017</c>: inline for PDF, and only for PDF.</summary>
    [Fact]
    public async Task A_preview_url_serves_the_pdf_inline()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var submission = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf"));

        var url = await ReadAsync<FileUrlDto>(
            await mentor.GetAsync($"/api/v1/submissions/{submission.Id}/preview-url"));

        using var anonymous = new HttpClient();
        var file = await anonymous.GetAsync(url.Url);

        file.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        file.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
    }

    [Fact]
    public async Task A_presentation_has_no_preview()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var submission = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, assignmentId, Pptx(), "презентация.pptx"));

        (await mentor.GetAsync($"/api/v1/submissions/{submission.Id}/preview-url"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_mentor_cannot_reach_another_mentors_file()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var submission = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf"));

        using var other = await SignInAsync("mentor2-head@mentortaskflow.test");

        (await other.GetAsync($"/api/v1/submissions/{submission.Id}/download-url"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// <c>TEN-065</c>: issuing a file belongs to one branch. In the all-branches mode there is nothing
    /// to attribute it to, so the answer is 400 rather than a file.
    /// </summary>
    [Fact]
    public async Task An_organization_admin_must_choose_a_branch_before_receiving_a_url()
    {
        var assignmentId = await PublishAssignmentAsync();

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var submission = await ReadAsync<SubmissionDto>(
            await UploadAsync(mentor, assignmentId, Pdf(), "работа.pdf"));

        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        var refused = await admin.GetAsync($"/api/v1/submissions/{submission.Id}/download-url");
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(refused)).ShouldBe(ErrorCodes.BranchContextRequired);

        admin.DefaultRequestHeaders.Add("X-MTF-Branch-Id", _headOfficeId.ToString());

        (await admin.GetAsync($"/api/v1/submissions/{submission.Id}/download-url"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------
    // Test files
    // -----------------------------------------------------------------

    private static byte[] Pdf(string body = "содержимое работы") =>
        Encoding.UTF8.GetBytes($"%PDF-1.7\n{body}\ntrailer\n%%EOF\n");

    /// <summary>A well-formed ZIP that is not an OPC package — the renamed-archive case.</summary>
    private static byte[] PlainZip()
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open());
            writer.Write("обычный архив");
        }

        return buffer.ToArray();
    }

    private static byte[] Pptx(bool declaresPresentationType = true, bool traversalEntry = false)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var contentType = declaresPresentationType
                ? "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"
                : "application/xml";

            Write(archive, "[Content_Types].xml",
                $"""<?xml version="1.0"?><Types><Override ContentType="{contentType}"/></Types>""");

            Write(archive, "ppt/presentation.xml", "<presentation/>");

            if (traversalEntry)
            {
                Write(archive, "../escaped.xml", "<escaped/>");
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Highly compressible padding: small on the wire, far over the ratio limit expanded.</summary>
    private static byte[] ZipBomb()
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml", "<Types/>");

            using var entry = archive.CreateEntry("ppt/presentation.xml", CompressionLevel.SmallestSize).Open();
            entry.Write(new byte[64 * 1024 * 1024]);
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        Guid assignmentId,
        byte[] content,
        string fileName)
    {
        // Awaited inside the using: returning the task would dispose the multipart content while the
        // test host was still reading it.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);

        file.Headers.ContentType = new MediaTypeHeaderValue(
            Path.GetExtension(fileName) is ".pdf"
                ? "application/pdf"
                : "application/octet-stream");

        form.Add(file, "file", fileName);

        return await client.PostAsync($"/api/v1/assignments/{assignmentId}/submissions", form);
    }

    private async Task<Guid> PublishAssignmentAsync(string title = "Задача ментора")
    {
        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");

        var draft = await ReadAsync<AssignmentDto>(await lead.PostAsJsonAsync("/api/v1/assignments/drafts",
            new CreateAssignmentDraftRequest(_mentorId, null, title, null, null)));

        var published = await ReadAsync<AssignmentDto>(await lead.PostAsJsonAsync(
            $"/api/v1/assignments/{draft.Id}/publish",
            new AssignmentActionRequest(draft.ConcurrencyToken)));

        return published.Id;
    }

    /// <summary>
    /// Drives the assignment back to <c>NeedsRework</c> so it accepts another version.
    /// </summary>
    /// <remarks>
    /// Through the domain rather than the API: <c>POST /submissions/{id}/reviews</c> arrives in the
    /// next phase, and the state machine is the same either way.
    /// </remarks>
    private async Task ReturnForReworkAsync(Guid assignmentId)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var assignment = await context.Assignments.SingleAsync(a => a.Id == assignmentId);
        var now = DateTimeOffset.UtcNow;

        assignment.StartReview(now);

        // Comfortably past InitialDueAt: ck_assignments_due_order forbids a rework deadline earlier
        // than the original one (ASN-026).
        assignment.RequestRework(now.AddDays(30), now);

        await context.SaveChangesAsync();
    }

    private async Task MarkOverdueAsync(Guid assignmentId)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var assignment = await context.Assignments.SingleAsync(a => a.Id == assignmentId);
        assignment.MarkOverdue(DateTimeOffset.UtcNow);

        await context.SaveChangesAsync();
    }

    private async Task MoveDeadlineIntoThePastAsync(Guid assignmentId)
    {
        await using var connection = await postgres.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();

        // Raw SQL: the deadline is immutable after publication by design, and the point here is a
        // clock that has moved on, not an edit the domain would allow. Both columns move together —
        // ck_assignments_due_order refuses a current deadline earlier than the initial one.
        command.CommandText = """
            UPDATE assignments
            SET initial_due_at = now() - interval '3 minutes',
                current_due_at = now() - interval '3 minutes'
            WHERE id = @id
            """;
        command.Parameters.AddWithValue("id", assignmentId);

        await command.ExecuteNonQueryAsync();
    }

    private async Task SetAllowLateSubmissionAsync(bool allowed)
    {
        await using var connection = await postgres.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = "UPDATE category_settings SET allow_late_submission = @allowed";
        command.Parameters.AddWithValue("allowed", allowed);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> LeadIdAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        return await context.Users
            .Where(u => u.Email == "lead-sharp@mentortaskflow.test")
            .Select(u => u.Id)
            .SingleAsync();
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ValidPassword));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Authorization =
            new("Bearer", (await ReadAsync<LoginResponse>(response)).AccessToken);

        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<T>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private async Task SeedAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var passwordHash = new Pbkdf2PasswordHasher().Hash(ValidPassword);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Seeded);
        context.Branches.Add(headOffice);

        var sharp = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        context.Categories.Add(sharp);
        context.CategorySettings.Add(CategorySettings.CreateDefault(sharp, headOffice.TimeZoneId, Seeded));

        var organizationAdmin = User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded);
        var branchAdmin = User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded);
        var lead = User.CreateLead(organization.Id, headOffice.Id, sharp.Id, "Лид C#", "lead-sharp@mentortaskflow.test", Seeded);
        var mentor = User.CreateMentor(organization.Id, headOffice.Id, sharp.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded);
        var second = User.CreateMentor(organization.Id, headOffice.Id, sharp.Id, "Второй ментор", "mentor2-head@mentortaskflow.test", Seeded);

        var users = new List<User> { organizationAdmin, branchAdmin, lead, mentor, second };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        _organizationId = organization.Id;
        _headOfficeId = headOffice.Id;
        _sharpId = sharp.Id;
        _mentorId = mentor.Id;
    }
}
