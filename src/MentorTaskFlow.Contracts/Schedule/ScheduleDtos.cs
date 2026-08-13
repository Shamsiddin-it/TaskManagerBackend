using MentorTaskFlow.Contracts.Tenancy;

namespace MentorTaskFlow.Contracts.Schedule;

/// <summary>One day of the curriculum inside a category (TZ 10.4).</summary>
public sealed record TopicDto(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    Guid CategoryId,
    int DayNumber,
    DateOnly? PlannedDate,
    string Title,
    string? Description,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken,
    BranchSummaryDto? Branch);

/// <summary>
/// <c>POST /topics</c>.
/// </summary>
/// <remarks>
/// No scope fields. The category is named, and its organization and branch are derived from it, so a
/// client cannot place a topic anywhere its category does not already live (<c>SEC-003</c>).
/// </remarks>
public sealed record CreateTopicRequest(
    Guid CategoryId,
    int DayNumber,
    DateOnly? PlannedDate,
    string Title,
    string? Description);

/// <summary>
/// <c>PUT /topics/{id}</c>.
/// </summary>
/// <remarks>
/// The category is not editable: moving a topic between categories would carry its templates and any
/// assignments already generated from them into a different study stream.
/// </remarks>
public sealed record UpdateTopicRequest(
    int DayNumber,
    DateOnly? PlannedDate,
    string Title,
    string? Description,
    string ConcurrencyToken);

/// <summary>A work template attached to a topic (TZ 10.5).</summary>
public sealed record TopicAssignmentDto(
    Guid Id,
    Guid TopicId,
    Guid OrganizationId,
    Guid BranchId,
    Guid CategoryId,
    string Type,
    string Title,
    string? Description,
    bool IsRequired,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken);

/// <summary>
/// <c>POST /topics/{topicId}/assignments</c>.
/// </summary>
/// <remarks>
/// Scope is inherited from the topic in the route and is absent from the body: that is what makes
/// <c>TPL-001</c> enforceable rather than merely stated (<c>TPL-005</c>).
/// </remarks>
public sealed record CreateTopicAssignmentRequest(
    string Type,
    string Title,
    string? Description,
    bool IsRequired = true);

/// <summary><c>PUT /topic-assignments/{id}</c>.</summary>
/// <remarks>
/// Editing a template does not touch assignments already created from it — title and description were
/// copied at creation and are frozen there (<c>TPL-004</c>, 10.6.1).
/// </remarks>
public sealed record UpdateTopicAssignmentRequest(
    string Type,
    string Title,
    string? Description,
    bool IsRequired,
    string ConcurrencyToken);

/// <summary>Body of the activate and deactivate actions.</summary>
public sealed record ScheduleActionRequest(string ConcurrencyToken);

/// <summary>Filters for <c>GET /topics</c>.</summary>
public sealed record TopicListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    public Guid? CategoryId { get; init; }

    /// <summary>Null returns the archived topics alongside the live ones (<c>TOPIC-013</c>).</summary>
    public bool? IsActive { get; init; }

    /// <summary>Whitelisted: <c>dayNumber</c>, <c>plannedDate</c>, <c>title</c> (<c>API-004</c>).</summary>
    public string? Sort { get; init; }

    public string? Order { get; init; }
}
