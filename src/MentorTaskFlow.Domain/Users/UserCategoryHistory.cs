using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Tenancy;

namespace MentorTaskFlow.Domain.Users;

/// <summary>
/// Append-only journal of category transfers <b>within one branch</b> (TZ 10.13).
/// </summary>
/// <remarks>
/// A transfer that also changes branch additionally produces a <see cref="UserBranchHistory"/> row in
/// the same transaction (<c>USER-025</c>). <c>UPDATE</c> and <c>DELETE</c> are revoked from the
/// application database role, so «append-only» is a database guarantee, not a coding rule
/// (<c>USER-026</c>, TZ 12.6).
/// </remarks>
public sealed class UserCategoryHistory : BaseEntity
{
    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 500;

    public Guid UserId { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>The branch the transfer happened inside; immutable.</summary>
    public Guid BranchId { get; private set; }

    public Guid? PreviousCategoryId { get; private set; }

    public Guid? NewCategoryId { get; private set; }

    /// <summary>Recorded because a category change may come with a role change.</summary>
    public UserRole PreviousRole { get; private set; }

    public UserRole NewRole { get; private set; }

    public Guid ChangedById { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    public string Reason { get; private set; } = null!;

    /// <summary>Ties this row to the AuditLog entry and notifications of the same operation.</summary>
    public Guid CorrelationId { get; private set; }

    private UserCategoryHistory()
    {
    }

    public static UserCategoryHistory Record(
        Guid userId,
        Guid organizationId,
        Guid branchId,
        Guid? previousCategoryId,
        Guid? newCategoryId,
        UserRole previousRole,
        UserRole newRole,
        Guid changedById,
        string reason,
        Guid correlationId,
        DateTimeOffset now)
    {
        var trimmedReason = (reason ?? string.Empty).Trim();

        if (trimmedReason.Length is < ReasonMinLength or > ReasonMaxLength)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                $"Причина должна содержать от {ReasonMinLength} до {ReasonMaxLength} символов.");
        }

        return new UserCategoryHistory
        {
            UserId = userId,
            OrganizationId = organizationId,
            BranchId = branchId,
            PreviousCategoryId = previousCategoryId,
            NewCategoryId = newCategoryId,
            PreviousRole = previousRole,
            NewRole = newRole,
            ChangedById = changedById,
            ChangedAt = now,
            Reason = trimmedReason,
            CorrelationId = correlationId,
            CreatedAt = now,
        };
    }
}
