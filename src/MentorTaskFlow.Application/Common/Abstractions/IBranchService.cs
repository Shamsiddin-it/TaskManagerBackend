using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Tenancy;

namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>Organization profile (Приложение D.0).</summary>
public interface IOrganizationService
{
    /// <summary>
    /// Reads the caller's own organization.
    /// </summary>
    /// <remarks>
    /// Any authenticated user may call this and receives the minimal view; only an Organization Admin
    /// gets the full one. The identifier is never a parameter — it comes from the principal, so there
    /// is no way to ask about anybody else's organization (<c>ORG-003</c>, <c>ORG-021</c>).
    /// </remarks>
    Task<object> GetAsync(CancellationToken cancellationToken);

    Task<OrganizationDto> UpdateAsync(UpdateOrganizationRequest request, CancellationToken cancellationToken);
}

/// <summary>Branch lifecycle (TZ 39.1–39.3, Приложение D.0).</summary>
public interface IBranchService
{
    /// <summary>
    /// Lists the branches of the organization — Organization Admin only.
    /// </summary>
    /// <remarks>
    /// Branch Admin, Lead and Mentor get 403 rather than a filtered list: the roster of branches
    /// reveals the composition of the organization and lies outside branch isolation (<c>BRN-006</c>,
    /// <c>TEN-003</c>).
    /// </remarks>
    Task<PagedResult<BranchDto>> ListAsync(BranchListQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one branch.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="BranchDto"/> to an administrator and <see cref="BranchSummaryDto"/> to a Lead
    /// or Mentor asking about their own branch. Any other identifier answers 404 — identical to a
    /// branch that does not exist, so its existence stays unconfirmed (<c>BRN-007</c>…<c>BRN-009</c>).
    /// </remarks>
    Task<object> GetAsync(Guid branchId, CancellationToken cancellationToken);

    Task<BranchDto> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken);

    Task<BranchDto> UpdateAsync(Guid branchId, UpdateBranchRequest request, CancellationToken cancellationToken);

    Task<BranchDto> ActivateAsync(Guid branchId, BranchActionRequest request, CancellationToken cancellationToken);

    Task<BranchDto> DeactivateAsync(Guid branchId, DeactivateBranchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Transfers the head-office flag (TZ 39.3).
    /// </summary>
    /// <remarks>
    /// Clearing and setting happen in one transaction, in that order — the reverse would violate
    /// <c>ux_branches_single_head_office</c> mid-transaction — serialised by a row lock on the
    /// organization so two concurrent calls cannot both succeed (<c>BRN-035</c>, <c>BRN-046</c>).
    /// </remarks>
    Task<BranchDto> MakeHeadOfficeAsync(Guid branchId, BranchActionRequest request, CancellationToken cancellationToken);
}
