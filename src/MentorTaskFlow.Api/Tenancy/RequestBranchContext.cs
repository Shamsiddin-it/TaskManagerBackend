using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Application.Common.Tenancy;

namespace MentorTaskFlow.Api.Tenancy;

/// <summary>
/// Scoped, request-lifetime implementation of <see cref="IBranchContext"/>, populated once by
/// <see cref="BranchContextMiddleware"/>.
/// </summary>
public sealed class RequestBranchContext : IBranchContext
{
    private Guid? _organizationId;
    private Action? _onMissingBranchForMutation;

    public Guid EffectiveOrganizationId =>
        _organizationId ?? throw new InvalidOperationException(
            "Branch context has not been established for this request. " +
            "Endpoints that read tenant data require authentication (SEC-001).");

    public Guid? EffectiveBranchId { get; private set; }

    public bool IsAllBranchesReadContext { get; private set; }

    public bool CanOverrideBranch { get; private set; }

    public bool IsEstablished => _organizationId is not null;

    internal void Establish(
        Guid organizationId,
        Guid? effectiveBranchId,
        bool canOverrideBranch,
        Action onMissingBranchForMutation)
    {
        _organizationId = organizationId;
        EffectiveBranchId = effectiveBranchId;
        CanOverrideBranch = canOverrideBranch;

        // The all-branches context exists only for an Organization Admin who did not pick a branch.
        // For every other role a null branch would be a bug, not an aggregate view.
        IsAllBranchesReadContext = canOverrideBranch && effectiveBranchId is null;

        _onMissingBranchForMutation = onMissingBranchForMutation;
    }

    /// <inheritdoc />
    public Guid RequireBranchForMutation()
    {
        if (EffectiveBranchId is { } branchId)
        {
            return branchId;
        }

        _onMissingBranchForMutation?.Invoke();
        throw new BranchContextRequiredException();
    }
}
