namespace MentorTaskFlow.Domain.Auditing;

/// <summary>
/// Stable <c>AuditLog.Action</c> codes (TZ 10.14).
/// </summary>
/// <remarks>
/// The value never changes meaning between versions: incident reviews and retention reports filter on
/// it, so renaming one would silently orphan the history it used to describe.
/// </remarks>
public static class AuditActions
{
    // Organization and branch lifecycle
    public const string OrganizationUpdate = "organization.update";
    public const string BranchCreate = "branch.create";
    public const string BranchUpdate = "branch.update";
    public const string BranchActivate = "branch.activate";
    public const string BranchDeactivate = "branch.deactivate";
    public const string BranchMakeHeadOffice = "branch.make_head_office";
    public const string BranchTimeZoneChange = "branch.timezone_change";

    // Users
    public const string UserCreate = "user.create";
    public const string UserCreateOrganizationAdmin = "user.create_organization_admin";
    public const string UserUpdate = "user.update";
    public const string UserActivate = "user.activate";
    public const string UserDeactivate = "user.deactivate";
    public const string UserChangeRole = "user.change_role";
    public const string UserChangeAdminScope = "user.change_admin_scope";
    public const string UserChangeCategory = "user.change_category";
    public const string UserChangeBranch = "user.change_branch";

    /// <summary>
    /// Branch-scoped derivatives of <see cref="UserChangeBranch"/>.
    /// </summary>
    /// <remarks>
    /// A Branch Admin must not see the organization-level transfer record, which would name the other
    /// branch and thereby disclose the composition of the organization. They see one of these instead:
    /// user id, timestamp, and the fact of a transfer, with no counterpart branch (<c>TEN-049</c>).
    /// </remarks>
    public const string UserLeftBranch = "user.left_branch";

    public const string UserJoinedBranch = "user.joined_branch";

    // Categories and schedule
    public const string CategoryCreate = "category.create";
    public const string CategoryUpdate = "category.update";
    public const string CategoryActivate = "category.activate";
    public const string CategoryDeactivate = "category.deactivate";
    public const string CategorySettingsUpdate = "category.settings_update";

    public const string TopicCreate = "topic.create";
    public const string TopicUpdate = "topic.update";
    public const string TopicDelete = "topic.delete";
    public const string TopicAssignmentCreate = "topic_assignment.create";
    public const string TopicAssignmentUpdate = "topic_assignment.update";
    public const string TopicAssignmentDelete = "topic_assignment.delete";

    // Authentication
    public const string AuthLogin = "auth.login";
    public const string AuthLogout = "auth.logout";
    public const string AuthPasswordChange = "auth.password_change";
    public const string AuthPasswordReset = "auth.password_reset";
    public const string AuthPasswordSet = "auth.password_set";
    public const string AuthRefreshReuseDetected = "auth.refresh_reuse_detected";

    // Security
    public const string SecurityScopeOverrideRejected = "security.scope_override_rejected";
    public const string SecurityCrossScopeRejected = "security.cross_scope_rejected";

    // Assignments, notifications, storage, reports, AI
    public const string AssignmentForceCancel = "assignment.force_cancel";
    public const string SchedulerNoActiveMentor = "scheduler.no_active_mentor";
    public const string RetentionCleanup = "retention.cleanup";
    public const string NotificationRetry = "notification.retry";
    public const string TelegramBind = "telegram.bind";
    public const string TelegramUnbind = "telegram.unbind";
    public const string StorageOrphanCleanup = "storage.orphan_cleanup";
    public const string StorageCrossScopeInconsistency = "storage.cross_scope_inconsistency";
    public const string ReportOrganizationExport = "report.organization_export";
    public const string AiSummaryGenerate = "ai.summary_generate";
    public const string AuditRead = "audit.read";
    public const string BootstrapProvision = "bootstrap.provision";

    /// <summary>
    /// Actions that are <b>always</b> recorded without a branch: they concern the organization as a
    /// whole, so attributing them to whichever branch happened to be selected would be misleading.
    /// </summary>
    /// <remarks>
    /// Exactly the list of <c>TEN-048</c>. Creating a branch, changing an administrative contour or
    /// provisioning a tenant are not events "in" a branch, and a Branch Admin must not see them at
    /// all (<c>TEN-049</c>).
    /// </remarks>
    public static readonly IReadOnlySet<string> AlwaysOrganizationLevelActions = new HashSet<string>(StringComparer.Ordinal)
    {
        OrganizationUpdate,
        BranchCreate,
        BranchUpdate,
        BranchActivate,
        BranchDeactivate,
        BranchMakeHeadOffice,
        UserCreateOrganizationAdmin,
        UserChangeAdminScope,
        UserChangeBranch,
        SecurityScopeOverrideRejected,
        StorageCrossScopeInconsistency,
        ReportOrganizationExport,
        BootstrapProvision,
    };

    /// <summary>
    /// Every action for which <c>BranchId IS NULL</c> is permitted — the source of truth for
    /// <c>ck_audit_logs_branch_scope</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The migration is generated from this set, so the database and the code cannot drift.
    /// </para>
    /// <para>
    /// <b>Deviation from <c>TEN-048</c>, deliberate and flagged.</b> <see cref="AuditRead"/> is added
    /// to the list of <c>TEN-048</c>. The TZ contradicts itself here: <c>AUD-023</c> requires the
    /// record of a read to carry <c>branchFilter: "&lt;uuid&gt;|all"</c>, and <c>TEN-033</c> makes the
    /// all-branches mode the default for an Organization Admin who sends no header — yet with
    /// <c>audit.read</c> absent from <c>TEN-048</c>, that very read cannot be recorded, because it has
    /// no branch to name. The alternatives were worse: attributing the read to an arbitrary branch
    /// would falsify the audit trail, and refusing the read outright would remove a capability
    /// Приложение D.7 grants explicitly.
    /// </para>
    /// <para>
    /// Unlike the entries above, <see cref="AuditRead"/> is <b>not</b> forced to null: a Branch Admin's
    /// read carries their branch, so they can still see their own reads. Only the all-branches read
    /// has no branch to record.
    /// </para>
    /// <para>
    /// <see cref="AiSummaryGenerate"/> is added for the same reason and on the same terms.
    /// <c>TEN-078</c> defines an organization-level summary explicitly — <c>Scope='Organization'</c>,
    /// <c>branch_id IS NULL</c> in <c>ck_ai_summaries_scope_shape</c> — and <c>AI-021</c> requires
    /// every generation to be recorded. Without the entry those two rules contradict each other and
    /// the recording fails at the database. A branch-scoped summary still carries its branch.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> OrganizationLevelActions =
        new HashSet<string>(AlwaysOrganizationLevelActions, StringComparer.Ordinal)
        {
            AuditRead,
            AiSummaryGenerate,
        };
}
