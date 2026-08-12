using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Users;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>User administration (TZ 15.1, 39.5, Приложение D.2).</summary>
public interface IUserService
{
    /// <summary>
    /// Lists users within the caller's reach.
    /// </summary>
    /// <remarks>
    /// An Organization Admin sees the whole organization, a Branch Admin their branch, a Lead their
    /// own category. A Mentor has no access to the list at all — 403 (<c>USER-010</c>). The counter is
    /// computed under the same filter, so it cannot betray the existence of users outside the scope
    /// (<c>TEN-002</c>).
    /// </remarks>
    Task<PagedResult<UserDto>> ListAsync(UserListQuery query, CancellationToken cancellationToken);

    Task<UserDto> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a user and issues the invitation.
    /// </summary>
    /// <remarks>
    /// Who may create whom is fixed by <c>USER-031</c>. The decision the TZ singles out: a
    /// <b>Branch Admin cannot create another Branch Admin</b>. Letting a branch's administrative
    /// contour reproduce itself would take the composition of administrators out of the
    /// organization's control and create an escalation path invisible at organization level.
    /// </remarks>
    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken);

    Task<UserDto> PatchAsync(Guid userId, PatchUserRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Reactivates a user.
    /// </summary>
    /// <remarks>
    /// Requires the user's category to be active, and for a Lead that the category has no other active
    /// Lead — otherwise 409 <c>ACTIVE_LEAD_ALREADY_EXISTS</c> (<c>USER-007</c>).
    /// </remarks>
    Task<UserDto> ActivateAsync(Guid userId, UserActionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deactivates a user: sign-in is refused and every session ends.
    /// </summary>
    /// <remarks>
    /// Deactivating the only Lead of a category, or the only administrator of a branch, is allowed.
    /// Both leave an observable but valid state and raise a notification rather than being blocked:
    /// refusing would strand an organization whose sole administrator has left (<c>USER-005</c>,
    /// <c>USER-036</c>, <c>TEN-017</c>).
    /// </remarks>
    Task<UserDto> DeactivateAsync(Guid userId, UserActionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Changes role and, for an administrator, the contour (<c>USER-008</c>, <c>USER-033</c>).
    /// </summary>
    /// <remarks>
    /// Any change of access level invalidates issued tokens: <c>TokenVersion</c> is incremented and
    /// every refresh token is revoked, so the person signs in again with the new scope
    /// (<c>AUTH-034</c>).
    /// </remarks>
    Task<UserDto> ChangeRoleAsync(Guid userId, ChangeRoleRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Reissues the invitation, invalidating the previous link (<c>USER-009</c>, <c>AUTH-017</c>).
    /// </summary>
    Task ResendInvitationAsync(Guid userId, CancellationToken cancellationToken);
}
