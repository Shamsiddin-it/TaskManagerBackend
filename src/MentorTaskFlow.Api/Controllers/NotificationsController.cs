using MentorTaskFlow.Api.Authorization;
using MentorTaskFlow.Application.Common.Abstractions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Controllers;

/// <summary>
/// The outbox journal (Приложение D.7, TZ 18.4).
/// </summary>
/// <remarks>
/// This is one of the three ways a dead letter becomes known, and the only one inside the product.
/// The other two — the metric and the technical log — are deliberately outside the mail path, because
/// an unreachable mail provider is precisely the condition that produces dead letters (<c>NTF-023</c>).
/// </remarks>
[ApiController]
[Route("api/v1/admin/notifications")]
[Produces("application/json")]
public sealed class NotificationsController(INotificationAdminService notifications) : ControllerBase
{
    /// <summary><c>GET /admin/notifications</c> — within the caller's contour (<c>TEN-046</c>).</summary>
    [HttpGet]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<PagedResult<NotificationDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<NotificationDto>>> ListAsync(
        [FromQuery] NotificationListQuery query,
        CancellationToken cancellationToken) =>
        Ok(await notifications.ListAsync(query, cancellationToken));

    /// <summary>
    /// <c>POST /admin/notifications/{id}/retry</c> — send a dead-lettered notification again.
    /// </summary>
    /// <remarks>
    /// Only from <c>DeadLetter</c>, and the attempt counter starts over: a manual retry follows a human
    /// decision that the cause has been dealt with, so the row deserves the full budget rather than the
    /// one attempt its history left it (<c>NTF-014</c>).
    /// </remarks>
    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = MtfPolicies.AnyAdmin)]
    [ProducesResponseType<NotificationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationDto>> RetryAsync(Guid id, CancellationToken cancellationToken) =>
        Ok(await notifications.RetryAsync(id, cancellationToken));
}
