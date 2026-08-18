using System.Diagnostics.Metrics;
using System.Text;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Domain.Assignments;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Submissions;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Notifications;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using MentorTaskFlow.Infrastructure.Scheduling;
using MentorTaskFlow.Infrastructure.Storage;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio.DataModel.Args;

namespace MentorTaskFlow.IntegrationTests.Storage;

/// <summary>
/// The daily orphan sweep of TZ 17.7, against a real bucket.
/// </summary>
/// <remarks>
/// <para>
/// An upload writes the object first and the row second, so a failure between the two leaves an
/// object nothing points at (<c>SUB-030</c>, <c>SUB-031</c>). This job is the only thing that removes
/// it, and it is the one place in the storage layer that both lists and deletes — so it is where a
/// mistake costs files rather than a failed request.
/// </para>
/// <para>
/// The grace-period case is why this is an integration test rather than a unit test: the age of an
/// object comes from the listing the storage returns, and whether that field is populated at all is a
/// property of the storage, not of our code. Reading it wrongly deletes uploads that were still
/// committing their row.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class OrphanObjectCleanupTests(PostgresFixture postgres, MinioFixture minio) : IAsyncLifetime
{
    private static readonly DateTimeOffset Seeded = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Real «now», because the objects under test are written during the run.</summary>
    private static readonly DateTimeOffset RunAt = DateTimeOffset.UtcNow;

    private Guid _organizationId;
    private Guid _branchId;
    private Guid _categoryId;
    private Guid _mentorId;
    private Guid _leadId;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        await minio.ResetAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>An object nobody points at, past the grace period, is removed.</summary>
    [Fact]
    public async Task An_object_without_a_submission_row_is_removed()
    {
        var key = OrphanKey();
        await PutAsync(key);

        // The cutoff is «now minus OrphanTtlHours», so a run placed far enough ahead puts the object
        // behind it without the test waiting a day for the clock.
        await RunAsync(RunAt.AddHours(48));

        (await minio.ListKeysAsync()).ShouldNotContain(key);
    }

    /// <summary>
    /// <c>SUB-031</c>: an object younger than the grace period is left alone.
    /// </summary>
    /// <remarks>
    /// This is the assertion that catches a listing whose timestamp the code fails to read: with no
    /// age to compare, every object looks old, and an upload three seconds from committing its row is
    /// deleted underneath it.
    /// </remarks>
    [Fact]
    public async Task A_fresh_object_is_left_alone()
    {
        var key = OrphanKey();
        await PutAsync(key);

        await RunAsync(RunAt);

        (await minio.ListKeysAsync()).ShouldContain(key);
    }

    /// <summary>An object a submission row points at is never an orphan, however old it is.</summary>
    [Fact]
    public async Task An_object_with_a_submission_row_is_kept()
    {
        var key = await SeedSubmissionAsync();
        await PutAsync(key);

        await RunAsync(RunAt.AddHours(48));

        (await minio.ListKeysAsync()).ShouldContain(key);
    }

    /// <summary>The sweep records what it removed, against the branch whose prefix it swept (<c>TEN-067</c>).</summary>
    [Fact]
    public async Task A_removal_is_audited_against_its_branch()
    {
        await PutAsync(OrphanKey());

        await RunAsync(RunAt.AddHours(48));

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var entry = await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.StorageOrphanCleanup);
        entry.BranchId.ShouldBe(_branchId);
        entry.Metadata!.RootElement.GetProperty("deleted").GetInt32().ShouldBe(1);
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    private string OrphanKey() => SubmissionStorageKey.For(
        _organizationId,
        _branchId,
        _categoryId,
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        FileExtension.Pdf);

    private async Task PutAsync(string key)
    {
        using var client = minio.CreateClient();
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.7 test object"));

        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(MinioFixture.Bucket)
            .WithObject(key)
            .WithStreamData(content)
            .WithObjectSize(content.Length)
            .WithContentType("application/pdf"));
    }

    private async Task RunAsync(DateTimeOffset now)
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var clock = new FixedClock(now);

        var job = new OrphanObjectCleanupJob(
            context,
            minio.CreateClient(),
            new OutboxWriter(
                context,
                new StubBranchContext(_organizationId, _branchId),
                new NotificationMetrics(MeterFactory()),
                clock),
            new NoopAuditWriter(context, clock),
            Options.Create(new StorageOptions
            {
                Endpoint = minio.Endpoint,
                AccessKey = MinioFixture.AccessKey,
                SecretKey = MinioFixture.SecretKey,
                Bucket = MinioFixture.Bucket,
                OrphanTtlHours = 24,
            }),
            NullLogger<OrphanObjectCleanupJob>.Instance,
            clock);

        await job.RunAsync(CancellationToken.None);
    }

    private static IMeterFactory MeterFactory() =>
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    private async Task<string> SeedSubmissionAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var assignment = Assignment.CreateDraft(
            _organizationId, _branchId, _categoryId, _mentorId, _leadId, null,
            "Задача", null, Seeded.AddDays(3), Seeded.AddMinutes(-5));

        assignment.Publish(_leadId, Seeded);
        context.Assignments.Add(assignment);
        await context.SaveChangesAsync();

        var submittedAt = Seeded.AddHours(2);
        var submissionId = Guid.CreateVersion7();

        var submission = Submission.Record(
            submissionId,
            assignment,
            1,
            SubmissionStorageKey.For(_organizationId, _branchId, _categoryId, assignment.Id, submissionId, FileExtension.Pdf),
            "работа.pdf",
            FileExtension.Pdf,
            1024,
            new string('a', 64),
            false,
            _mentorId,
            submittedAt);

        context.Submissions.Add(submission);
        assignment.Submit(isFirstVersion: true, submittedAt);
        await context.SaveChangesAsync();

        return submission.StorageKey;
    }

    private async Task SeedAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Seeded);
        context.Branches.Add(headOffice);

        var sharp = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        context.Categories.Add(sharp);
        context.CategorySettings.Add(CategorySettings.CreateDefault(sharp, "Asia/Dushanbe", Seeded));

        var lead = User.CreateLead(organization.Id, headOffice.Id, sharp.Id, "Лид", "lead@mentortaskflow.test", Seeded);
        var mentor = User.CreateMentor(organization.Id, headOffice.Id, sharp.Id, "Ментор", "mentor@mentortaskflow.test", Seeded);
        context.Users.AddRange(lead, mentor);

        await context.SaveChangesAsync();

        _organizationId = organization.Id;
        _branchId = headOffice.Id;
        _categoryId = sharp.Id;
        _leadId = lead.Id;
        _mentorId = mentor.Id;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StubBranchContext(Guid organizationId, Guid branchId)
        : Application.Common.Tenancy.IBranchContext
    {
        public Guid EffectiveOrganizationId => organizationId;

        public Guid? EffectiveBranchId => branchId;

        public bool IsAllBranchesReadContext => false;

        public bool CanOverrideBranch => false;

        public Guid RequireBranchForMutation() => branchId;
    }

    /// <summary>Writes system audit rows without a request context, as a job does.</summary>
    private sealed class NoopAuditWriter(MentorTaskFlowDbContext context, IClock clock) : IAuditWriter
    {
        public void Write(AuditEntry entry) => throw new NotSupportedException("Jobs write system entries.");

        public void WriteSystem(AuditEntry entry, Guid organizationId, Guid? branchId, Guid? correlationId = null) =>
            context.AuditLogs.Add(AuditLog.Record(
                entry.Action,
                entry.EntityType,
                organizationId,
                branchId,
                entry.CategoryId,
                entry.EntityId,
                AuditActorType.System,
                actorId: null,
                actorRole: null,
                actorAdminScope: null,
                entry.Result,
                correlationId ?? Guid.CreateVersion7(),
                clock.UtcNow,
                failureReason: entry.FailureReason,
                metadata: entry.Metadata));
    }
}
