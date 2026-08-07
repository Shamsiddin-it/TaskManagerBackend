namespace MentorTaskFlow.Domain.Notifications;

/// <summary>
/// The 19 notification event types of Приложение E.
/// </summary>
/// <remarks>
/// Section 18.2 and Приложение E must agree; a divergence is a defect in the document. The list is
/// reproduced here once so the code cannot add a nineteenth-and-a-half.
/// </remarks>
public static class NotificationEventTypes
{
    // Study cycle — always branch- and category-scoped.
    public const string AssignmentAssigned = "AssignmentAssigned";
    public const string AssignmentSuggested = "AssignmentSuggested";
    public const string AssignmentReassigned = "AssignmentReassigned";
    public const string SubmissionUploaded = "SubmissionUploaded";
    public const string LateSubmissionUploaded = "LateSubmissionUploaded";
    public const string ReviewApproved = "ReviewApproved";
    public const string ReviewNeedsRework = "ReviewNeedsRework";
    public const string DeadlineReminder = "DeadlineReminder";
    public const string AssignmentOverdue = "AssignmentOverdue";
    public const string AssignmentCancelled = "AssignmentCancelled";

    // Category-level.
    public const string SchedulerNoActiveMentor = "SchedulerNoActiveMentor";
    public const string CategoryWithoutLead = "CategoryWithoutLead";

    // Branch-level.
    public const string BranchDeactivated = "BranchDeactivated";
    public const string BranchActivated = "BranchActivated";

    /// <summary>Addressed to the user's <b>new</b> branch: they have already been transferred.</summary>
    public const string UserBranchChanged = "UserBranchChanged";

    // Organization-level.
    public const string BranchWithoutAdmin = "BranchWithoutAdmin";
    public const string OrganizationSystemAlert = "OrganizationSystemAlert";
    public const string NotificationDeadLetter = "NotificationDeadLetter";

    /// <summary>Invitation to set a first password, produced on user creation and on bootstrap.</summary>
    public const string UserInvitation = "UserInvitation";

    /// <summary>
    /// The only event types permitted to carry <c>BranchId IS NULL</c> (<c>TEN-042</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Source of truth for <c>ck_notification_outbox_branch_scope</c>. Each entry concerns the
    /// organization rather than any one branch: a branch left without an administrator is answered by
    /// the Organization Admin, and a system alert has no branch at all.
    /// </para>
    /// <para>
    /// <see cref="UserInvitation"/> is here because the bootstrap administrator is an Organization
    /// Admin with no branch (<c>DEPLOY-031</c>); an invitation to a Branch Admin, Lead or Mentor always
    /// carries their branch.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> OrganizationLevelEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        BranchWithoutAdmin,
        OrganizationSystemAlert,
        NotificationDeadLetter,
        UserInvitation,
    };
}
