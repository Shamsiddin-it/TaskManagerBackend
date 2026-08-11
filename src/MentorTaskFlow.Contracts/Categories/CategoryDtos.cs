using MentorTaskFlow.Contracts.Tenancy;

namespace MentorTaskFlow.Contracts.Categories;

/// <summary>
/// A study track inside one branch (TZ 10.2).
/// </summary>
/// <remarks>
/// <c>branch</c> is populated in the all-branches read context and is mandatory there: a row without
/// its branch in a cross-branch response is a contract defect, because two categories may share a
/// name and be told apart only by branch (<c>TEN-073</c>, <c>CAT-007</c>).
/// </remarks>
public sealed record CategoryDto(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    string Name,
    string? Description,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken,
    BranchSummaryDto? Branch);

/// <summary>
/// <c>POST /categories</c>.
/// </summary>
/// <remarks>
/// Neither <c>organizationId</c> nor <c>branchId</c> appears here. Both are decided by the server —
/// from the claim for a Branch Admin, from <c>X-MTF-Branch-Id</c> for an Organization Admin — and a
/// client that sends them gets 400 (<c>CAT-001</c>, <c>SEC-003</c>, <c>CAT-025</c>).
/// </remarks>
public sealed record CreateCategoryRequest(string Name, string? Description);

/// <summary><c>PUT /categories/{id}</c>.</summary>
public sealed record UpdateCategoryRequest(string Name, string? Description, string ConcurrencyToken);

/// <summary>
/// <c>POST /categories/{id}/deactivate</c>.
/// </summary>
/// <remarks>
/// <c>confirmActiveUsers</c> must be true while the category still has active users, otherwise the
/// answer is 409 <c>CATEGORY_HAS_ACTIVE_USERS</c>. The users are not deactivated along with it
/// (<c>CAT-003</c>).
/// </remarks>
public sealed record DeactivateCategoryRequest(string ConcurrencyToken, bool ConfirmActiveUsers = false);

/// <summary>Body of the remaining category actions.</summary>
public sealed record CategoryActionRequest(string ConcurrencyToken);

/// <summary>
/// Per-category settings (TZ 10.3).
/// </summary>
/// <remarks>
/// Readable by the Lead and Mentor of the category: the frontend needs the time zone and the
/// late-submission policy to render deadlines honestly (<c>CAT-006</c>).
/// </remarks>
public sealed record CategorySettingsDto(
    Guid CategoryId,
    string TimeZoneId,
    int DefaultAssignmentDueDays,
    TimeOnly DefaultDueTimeLocal,
    int DeadlineReminderHours,
    bool AllowLateSubmission,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken);

/// <summary>
/// <c>PUT /categories/{id}/settings</c>.
/// </summary>
/// <remarks>
/// Changing <c>timeZoneId</c>, <c>defaultAssignmentDueDays</c> or <c>defaultDueTimeLocal</c> does
/// <b>not</b> recompute the deadlines of assignments already published: those are stored as absolute
/// UTC moments, and the new values apply only to assignments created afterwards (<c>CAT-015</c>).
/// <c>allowLateSubmission</c> is the exception — it is read at upload time, so a change takes effect
/// immediately on existing assignments (<c>CAT-016</c>).
/// </remarks>
public sealed record UpdateCategorySettingsRequest(
    string TimeZoneId,
    int DefaultAssignmentDueDays,
    TimeOnly DefaultDueTimeLocal,
    int DeadlineReminderHours,
    bool AllowLateSubmission,
    string ConcurrencyToken);

/// <summary>Filters for <c>GET /categories</c> (<c>CAT-007</c>).</summary>
public sealed record CategoryListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    public bool? IsActive { get; init; }

    /// <summary>Whitelisted: <c>name</c>, <c>createdAt</c> (<c>API-004</c>).</summary>
    public string? Sort { get; init; }

    public string? Order { get; init; }
}
