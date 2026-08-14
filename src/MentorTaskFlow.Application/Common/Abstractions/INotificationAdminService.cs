using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Notifications;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>The outbox journal as an administrator operates it (TZ 18.4, Приложение D.7).</summary>
public interface INotificationAdminService
{
    /// <summary>
    /// Lists outbox rows within the caller's administrative contour (<c>TEN-046</c>).
    /// </summary>
    /// <remarks>
    /// A Branch Admin sees their branch and none of the organization-level rows. This is also one of
    /// the three ways a dead letter becomes visible without depending on the mail provider that is
    /// probably the reason it exists (<c>NTF-023</c>).
    /// </remarks>
    Task<PagedResult<NotificationDto>> ListAsync(NotificationListQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a dead-lettered row to the queue with its attempt count reset (<c>NTF-014</c>).
    /// </summary>
    /// <remarks>
    /// A row of another branch, or an organization-level row for a Branch Admin, answers 404 rather
    /// than 403 — its existence is not theirs to learn (<c>TEN-047</c>).
    /// </remarks>
    Task<NotificationDto> RetryAsync(Guid notificationId, CancellationToken cancellationToken);
}
