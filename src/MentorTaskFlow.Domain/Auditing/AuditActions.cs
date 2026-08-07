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
    /// The complete list of actions for which <c>BranchId IS NULL</c> is permitted (<c>TEN-048</c>).
    /// </summary>
    /// <remarks>
    /// This set is the single source of truth for the CHECK constraint
    /// <c>ck_audit_logs_branch_scope</c>: the migration is generated from it, so the database and the
    /// code cannot drift. Adding an entry means editing the TZ, not just this file (<c>TEN-010</c>).
    /// </remarks>
    public static readonly IReadOnlySet<string> OrganizationLevelActions = new HashSet<string>(StringComparer.Ordinal)
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
}
