using MentorTaskFlow.Domain.Tenancy;

namespace MentorTaskFlow.Application.Common.Tenancy;

/// <summary>
/// An immutable view of the authenticated principal, populated <b>only</b> from a validated access
/// token (<c>TEN-011a</c>, TZ 38.3).
/// </summary>
/// <remarks>
/// Nothing here is ever read from a request body, query string or header. That is the whole point:
/// <c>SEC-003</c> forbids accepting <c>OrganizationId</c>, <c>BranchId</c>, <c>CategoryId</c>,
/// <c>Role</c> or <c>AdminScope</c> from a client, and this interface is the only sanctioned source.
/// </remarks>
public interface ICurrentUserContext
{
    Guid UserId { get; }

    UserRole Role { get; }

    /// <summary>Null for Lead and Mentor.</summary>
    AdminScope? AdminScope { get; }

    /// <summary>Always present — a token without <c>org_id</c> is invalid (<c>AUTH-033</c>).</summary>
    Guid OrganizationId { get; }

    /// <summary>Null only for an Organization Admin.</summary>
    Guid? BranchId { get; }

    /// <summary>Null for both Admin contours.</summary>
    Guid? CategoryId { get; }

    int TokenVersion { get; }
}

/// <summary>
/// Resolves the current principal, or reports its absence.
/// </summary>
/// <remarks>
/// Anonymous endpoints exist (login, refresh, password reset, the Telegram webhook), so «no
/// principal» is a legitimate state rather than an error. Keeping it out of
/// <see cref="ICurrentUserContext"/> means business code that requires a principal cannot silently
/// receive a half-populated one.
/// </remarks>
public interface ICurrentUserAccessor
{
    ICurrentUserContext? Current { get; }

    bool IsAuthenticated { get; }
}
