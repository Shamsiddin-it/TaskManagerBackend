using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Notifications;
using MentorTaskFlow.Domain.Tenancy;

namespace MentorTaskFlow.UnitTests.Auditing;

/// <summary>
/// The branch-scope rules of <c>TEN-048</c> and <c>TEN-042</c>.
/// </summary>
/// <remarks>
/// <c>BranchId IS NULL</c> is allowed only for a closed list of organization-level actions and event
/// types. Widening either list requires changing the TZ, not just the code (<c>TEN-010</c>), and the
/// same lists generate the CHECK constraints, so code and database cannot drift.
/// </remarks>
public sealed class AuditScopeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid BranchId = Guid.CreateVersion7();
    private static readonly Guid ActorId = Guid.CreateVersion7();

    [Theory]
    [InlineData(AuditActions.BranchCreate)]
    [InlineData(AuditActions.UserChangeBranch)]
    [InlineData(AuditActions.BootstrapProvision)]
    [InlineData(AuditActions.OrganizationUpdate)]
    public void An_organization_level_action_may_omit_the_branch(string action)
    {
        var log = Record(action, branchId: null);

        log.BranchId.ShouldBeNull();
        log.OrganizationId.ShouldBe(Org);
    }

    [Theory]
    [InlineData(AuditActions.CategoryCreate)]
    [InlineData(AuditActions.UserDeactivate)]
    [InlineData(AuditActions.AssignmentForceCancel)]
    [InlineData(AuditActions.NotificationRetry)]
    public void Every_other_action_requires_a_branch(string action)
    {
        Should.Throw<DomainException>(() => Record(action, branchId: null))
            .Code.ShouldBe(DomainErrorCodes.ValidationFailed);

        Should.NotThrow(() => Record(action, BranchId));
    }

    /// <summary>An organization is always present: even a background task runs for one (<c>AUD-021</c>).</summary>
    [Fact]
    public void An_organization_is_always_required()
    {
        Should.Throw<DomainException>(() => AuditLog.Record(
            AuditActions.BootstrapProvision,
            "Organization",
            Guid.Empty,
            null, null, null,
            AuditActorType.System,
            null, null, null,
            AuditResult.Success,
            Guid.CreateVersion7(),
            Now));
    }

    /// <summary>A system action has no actor; a user action must have one.</summary>
    [Fact]
    public void The_actor_shape_matches_the_actor_type()
    {
        Should.Throw<DomainException>(() => AuditLog.Record(
            AuditActions.StorageOrphanCleanup,
            "Submission",
            Org, BranchId, null, null,
            AuditActorType.System,
            actorId: ActorId,
            actorRole: null,
            actorAdminScope: null,
            AuditResult.Success,
            Guid.CreateVersion7(),
            Now));
    }

    /// <summary>Long values are truncated rather than rejected: an audit row must never be lost to a length check.</summary>
    [Fact]
    public void Oversized_request_metadata_is_truncated()
    {
        var log = AuditLog.Record(
            AuditActions.UserCreate,
            "User",
            Org, BranchId, null, null,
            AuditActorType.User,
            ActorId,
            UserRole.Admin,
            AdminScope.Branch,
            AuditResult.Success,
            Guid.CreateVersion7(),
            Now,
            path: new string('p', 400),
            userAgent: new string('u', 400));

        log.Path!.Length.ShouldBe(AuditLog.PathMaxLength);
        log.UserAgent!.Length.ShouldBe(AuditLog.UserAgentMaxLength);
    }

    /// <summary>Retention drops personal data while keeping the record of the action (TZ 27.5).</summary>
    [Fact]
    public void Forgetting_the_request_origin_keeps_the_action()
    {
        var log = AuditLog.Record(
            AuditActions.AuthLogin,
            "User",
            Org, BranchId, null, ActorId,
            AuditActorType.User,
            ActorId,
            UserRole.Mentor,
            null,
            AuditResult.Success,
            Guid.CreateVersion7(),
            Now,
            ipAddress: "10.0.0.1",
            userAgent: "Mozilla/5.0");

        log.ForgetRequestOrigin();

        log.IpAddress.ShouldBeNull();
        log.UserAgent.ShouldBeNull();
        log.Action.ShouldBe(AuditActions.AuthLogin);
        log.ActorId.ShouldBe(ActorId);
    }

    // -----------------------------------------------------------------
    // Outbox
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(NotificationEventTypes.BranchWithoutAdmin)]
    [InlineData(NotificationEventTypes.OrganizationSystemAlert)]
    [InlineData(NotificationEventTypes.NotificationDeadLetter)]
    [InlineData(NotificationEventTypes.UserInvitation)]
    public void An_organization_level_event_may_omit_the_branch(string eventType)
    {
        Should.NotThrow(() => Enqueue(eventType, branchId: null));
    }

    [Theory]
    [InlineData(NotificationEventTypes.AssignmentAssigned)]
    [InlineData(NotificationEventTypes.CategoryWithoutLead)]
    [InlineData(NotificationEventTypes.BranchDeactivated)]
    [InlineData(NotificationEventTypes.UserBranchChanged)]
    public void Every_other_event_requires_a_branch(string eventType)
    {
        Should.Throw<DomainException>(() => Enqueue(eventType, branchId: null));
        Should.NotThrow(() => Enqueue(eventType, BranchId));
    }

    [Fact]
    public void A_queued_notification_starts_pending_with_no_attempts()
    {
        var outbox = Enqueue(NotificationEventTypes.AssignmentAssigned, BranchId);

        outbox.Status.ShouldBe(NotificationStatus.Pending);
        outbox.Attempts.ShouldBe(0);
        outbox.NextAttemptAt.ShouldBe(Now);
        outbox.SentAt.ShouldBeNull();
    }

    /// <summary>
    /// The two lists are the source of truth for the CHECK constraints, so they must stay closed:
    /// a stray addition would let branch-scoped data be written without a branch.
    /// </summary>
    [Fact]
    public void The_organization_level_lists_match_the_specification()
    {
        // The 13 of TEN-048, verbatim.
        AuditActions.AlwaysOrganizationLevelActions.Count.ShouldBe(13);

        // Those 13 plus audit.read and ai.summary_generate — the two documented deviations, each for
        // a rule elsewhere in the TZ that defines a branchless action TEN-048 does not list. See
        // AuditActions.OrganizationLevelActions.
        AuditActions.OrganizationLevelActions.Count.ShouldBe(15);
        AuditActions.OrganizationLevelActions.ShouldContain(AuditActions.AuditRead);
        AuditActions.OrganizationLevelActions.ShouldContain(AuditActions.AiSummaryGenerate);

        NotificationEventTypes.OrganizationLevelEvents.Count.ShouldBe(4);
    }

    /// <summary>
    /// <c>audit.read</c> may omit the branch but is not forced to: a Branch Admin's read carries their
    /// branch, so they can still see their own reads (<c>TEN-049</c> excludes only branchless rows).
    /// </summary>
    [Fact]
    public void An_audit_read_is_recordable_with_and_without_a_branch()
    {
        Should.NotThrow(() => Record(AuditActions.AuditRead, BranchId));
        Should.NotThrow(() => Record(AuditActions.AuditRead, branchId: null));

        AuditActions.AlwaysOrganizationLevelActions.ShouldNotContain(AuditActions.AuditRead);
    }

    /// <summary>
    /// <c>ai.summary_generate</c> follows the same rule: branchless for the organization aggregate of
    /// <c>TEN-078</c>, and carrying its branch for every other scope.
    /// </summary>
    [Fact]
    public void An_ai_summary_is_recordable_with_and_without_a_branch()
    {
        Should.NotThrow(() => Record(AuditActions.AiSummaryGenerate, BranchId));
        Should.NotThrow(() => Record(AuditActions.AiSummaryGenerate, branchId: null));

        AuditActions.AlwaysOrganizationLevelActions.ShouldNotContain(AuditActions.AiSummaryGenerate);
    }

    private static AuditLog Record(string action, Guid? branchId) =>
        AuditLog.Record(
            action,
            "TestEntity",
            Org,
            branchId,
            null,
            null,
            AuditActorType.User,
            ActorId,
            UserRole.Admin,
            AdminScope.Organization,
            AuditResult.Success,
            Guid.CreateVersion7(),
            Now);

    private static NotificationOutbox Enqueue(string eventType, Guid? branchId) =>
        NotificationOutbox.Enqueue(
            Guid.CreateVersion7(),
            Org,
            branchId,
            null,
            NotificationChannel.Email,
            eventType,
            System.Text.Json.JsonDocument.Parse("{}"),
            $"key:{eventType}",
            Now);
}
