using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.Domain.Users;

/// <summary>
/// Append-only journal of branch transfers (TZ 10.19).
/// </summary>
/// <remarks>
/// There is a single <see cref="OrganizationId"/> rather than an old/new pair: moving a user between
/// organizations is impossible (<c>ORG-023</c>). The row is written in the same transaction as the
/// change to <c>User.BranchId</c>; its absence is a defect checked by <c>TEST-TEN-034</c>
/// (<c>BRN-027</c>).
/// </remarks>
public sealed class UserBranchHistory : BaseEntity
{
    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 500;

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>Null only when the user previously had no branch (an Organization Admin gaining one).</summary>
    public Guid? OldBranchId { get; private set; }

    /// <summary>Null only when the user moves into the Organization Admin contour.</summary>
    public Guid? NewBranchId { get; private set; }

    public Guid? OldCategoryId { get; private set; }

    public Guid? NewCategoryId { get; private set; }

    public Guid ChangedById { get; private set; }

    public string Reason { get; private set; } = null!;

    public DateTimeOffset ChangedAt { get; private set; }

    /// <summary>Ties this row to the AuditLog entry, the token revocation and the user's notification.</summary>
    public Guid CorrelationId { get; private set; }

    private UserBranchHistory()
    {
    }

    public static UserBranchHistory Record(
        Guid organizationId,
        Guid userId,
        Guid? oldBranchId,
        Guid? newBranchId,
        Guid? oldCategoryId,
        Guid? newCategoryId,
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

        // Mirrors ck_user_branch_history_change: a «transfer to the same branch» row is never created.
        if (oldBranchId == newBranchId)
        {
            throw new DomainException(
                DomainErrorCodes.ValidationFailed,
                "Старый и новый филиал должны различаться (BRN-026).");
        }

        return new UserBranchHistory
        {
            OrganizationId = organizationId,
            UserId = userId,
            OldBranchId = oldBranchId,
            NewBranchId = newBranchId,
            OldCategoryId = oldCategoryId,
            NewCategoryId = newCategoryId,
            ChangedById = changedById,
            Reason = trimmedReason,
            ChangedAt = now,
            CorrelationId = correlationId,
            CreatedAt = now,
        };
    }
}
