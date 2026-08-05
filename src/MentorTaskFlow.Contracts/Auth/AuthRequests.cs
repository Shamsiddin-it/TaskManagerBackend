namespace MentorTaskFlow.Contracts.Auth;

/// <summary>
/// <c>POST /auth/login</c>.
/// </summary>
/// <remarks>
/// No organization or branch selector. Sign-in is Email + password with no tenant choice, which is
/// exactly why <c>NormalizedEmail</c> is globally unique rather than unique per organization
/// (<c>TEN-028</c>).
/// </remarks>
public sealed record LoginRequest(string Email, string Password);

/// <summary><c>POST /auth/change-password</c> — requires the current password (<c>AUTH-014</c>).</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// <c>POST /auth/forgot-password</c>.
/// </summary>
/// <remarks>
/// Always answered with 202 and an identical body whether or not the address exists, so the endpoint
/// cannot be used to enumerate accounts (<c>AUTH-015</c>).
/// </remarks>
public sealed record ForgotPasswordRequest(string Email);

/// <summary><c>POST /auth/reset-password</c> — token from the emailed link, TTL 30 minutes.</summary>
public sealed record ResetPasswordRequest(string Token, string NewPassword);

/// <summary><c>POST /auth/set-password</c> — the invitation flow, TTL 24 hours (<c>AUTH-020</c>).</summary>
public sealed record SetPasswordRequest(string Token, string NewPassword);
