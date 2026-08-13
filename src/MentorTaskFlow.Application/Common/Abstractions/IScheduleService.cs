using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Schedule;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>The category schedule: topics and their work templates (TZ 15.4, Приложение D.4).</summary>
public interface IScheduleService
{
    /// <summary>
    /// Lists topics within the caller's reach.
    /// </summary>
    /// <remarks>
    /// A Mentor reads the schedule of their own category and may not change it — the plan is the
    /// Lead's instrument (<c>TOPIC-004</c>).
    /// </remarks>
    Task<PagedResult<TopicDto>> ListTopicsAsync(TopicListQuery query, CancellationToken cancellationToken);

    Task<TopicDto> GetTopicAsync(Guid topicId, CancellationToken cancellationToken);

    Task<TopicDto> CreateTopicAsync(CreateTopicRequest request, CancellationToken cancellationToken);

    Task<TopicDto> UpdateTopicAsync(Guid topicId, UpdateTopicRequest request, CancellationToken cancellationToken);

    Task<TopicDto> ActivateTopicAsync(Guid topicId, ScheduleActionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Archives a topic (<c>TOPIC-013</c>).
    /// </summary>
    /// <remarks>
    /// The supported alternative to deletion once a topic is referenced: it stops feeding
    /// auto-generation and stops appearing when creating assignments, while remaining visible in the
    /// schedule.
    /// </remarks>
    Task<TopicDto> DeactivateTopicAsync(Guid topicId, ScheduleActionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a topic outright.
    /// </summary>
    /// <remarks>
    /// Permitted only while nothing references it. A topic with templates or assignments answers 409
    /// <c>RESOURCE_IN_USE</c> and must be deactivated instead (<c>TOPIC-003</c>, <c>TOPIC-012</c>).
    /// </remarks>
    Task DeleteTopicAsync(Guid topicId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TopicAssignmentDto>> ListTopicAssignmentsAsync(Guid topicId, CancellationToken cancellationToken);

    Task<TopicAssignmentDto> GetTopicAssignmentAsync(Guid topicAssignmentId, CancellationToken cancellationToken);

    Task<TopicAssignmentDto> CreateTopicAssignmentAsync(
        Guid topicId,
        CreateTopicAssignmentRequest request,
        CancellationToken cancellationToken);

    Task<TopicAssignmentDto> UpdateTopicAssignmentAsync(
        Guid topicAssignmentId,
        UpdateTopicAssignmentRequest request,
        CancellationToken cancellationToken);

    Task<TopicAssignmentDto> ActivateTopicAssignmentAsync(
        Guid topicAssignmentId,
        ScheduleActionRequest request,
        CancellationToken cancellationToken);

    Task<TopicAssignmentDto> DeactivateTopicAssignmentAsync(
        Guid topicAssignmentId,
        ScheduleActionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a template, or refuses with 409 <c>RESOURCE_IN_USE</c> when assignments exist
    /// (<c>TPL-002</c>).
    /// </summary>
    Task DeleteTopicAssignmentAsync(Guid topicAssignmentId, CancellationToken cancellationToken);
}
