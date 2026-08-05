namespace MentorTaskFlow.Domain.Identity;

/// <summary>
/// Why a refresh token stopped being valid (TZ 10.10).
/// </summary>
/// <remarks>
/// Every value except <see cref="Rotated"/> and <see cref="Logout"/> marks a change of scope or
/// access level, and each of those also increments <c>User.TokenVersion</c> (<c>AUTH-034</c>). Keeping
/// the reason lets an incident review tell «the session simply rolled over» apart from «the account
/// was locked out from under the holder».
/// </remarks>
public enum RefreshTokenRevocationReason
{
    /// <summary>Normal rotation on <c>POST /auth/refresh</c> (<c>AUTH-007</c>).</summary>
    Rotated = 0,

    /// <summary>An already-revoked token was presented; the whole family is gone (<c>AUTH-008</c>).</summary>
    ReuseDetected = 1,

    PasswordChanged = 2,
    RoleChanged = 3,

    /// <summary>Added in 2.2 alongside <c>AdminScope</c> (<c>AUTH-035</c>).</summary>
    AdminScopeChanged = 4,

    CategoryChanged = 5,

    /// <summary>Added in 2.2 alongside branch transfers (<c>AUTH-035</c>).</summary>
    BranchChanged = 6,

    Deactivated = 7,
    Logout = 8,
}
