using System.Reflection;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Persistence;
using NetArchTest.Rules;

namespace MentorTaskFlow.ArchitectureTests;

/// <summary>
/// Structural guarantees of the tenancy model that no single feature test can express.
/// </summary>
/// <remarks>
/// The seed of <c>TEST-SEC-021</c> and <c>TEST-SEC-022</c>. The full «every handler carries an
/// explicit scope filter» rule needs handlers to exist, which starts in Phase 4; what can be enforced
/// from Phase 1 is that the persistence surface stays inside Infrastructure and that scope fields
/// cannot be reassigned from outside the domain.
/// </remarks>
public sealed class TenantScopeTests
{
    private static readonly Assembly Domain = typeof(BaseEntity).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;
    private static readonly Assembly Application = typeof(Application.Common.Abstractions.IClock).Assembly;

    /// <summary>
    /// <c>SEC-031</c>: reaching <c>DbSet&lt;T&gt;</c> past a scope-applying repository is forbidden. The
    /// enforceable form today is that the context type itself never leaves Infrastructure — a
    /// controller holding a <c>MentorTaskFlowDbContext</c> is precisely how a query ends up without a
    /// tenant filter.
    /// </summary>
    [Fact]
    public void The_db_context_is_not_referenced_outside_infrastructure()
    {
        Types.InAssembly(Api)
            .That()
            .DoNotHaveName(nameof(Program))
            .ShouldNot()
            .HaveDependencyOn(typeof(MentorTaskFlowDbContext).FullName)
            .GetResult()
            .ShouldPass("Only Infrastructure may touch MentorTaskFlowDbContext (SEC-031).");

        Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOn(typeof(MentorTaskFlowDbContext).FullName)
            .GetResult()
            .ShouldPass("Application must reach persistence through abstractions only.");
    }

    /// <summary>
    /// Scope is an <b>immutable snapshot</b> (<c>TEN-018</c>, 10.6.4). A public setter would allow
    /// re-scoping a historical row and silently rewriting the analytics of two branches, so the
    /// setters stay private and only named domain operations may move a user.
    /// </summary>
    [Theory]
    [InlineData(typeof(Branch), nameof(Branch.OrganizationId))]
    [InlineData(typeof(Category), nameof(Category.OrganizationId))]
    [InlineData(typeof(Category), nameof(Category.BranchId))]
    [InlineData(typeof(CategorySettings), nameof(CategorySettings.OrganizationId))]
    [InlineData(typeof(CategorySettings), nameof(CategorySettings.BranchId))]
    [InlineData(typeof(User), nameof(User.OrganizationId))]
    [InlineData(typeof(User), nameof(User.BranchId))]
    [InlineData(typeof(User), nameof(User.CategoryId))]
    [InlineData(typeof(UserBranchHistory), nameof(UserBranchHistory.OrganizationId))]
    [InlineData(typeof(UserCategoryHistory), nameof(UserCategoryHistory.OrganizationId))]
    [InlineData(typeof(UserCategoryHistory), nameof(UserCategoryHistory.BranchId))]
    public void Scope_properties_have_no_public_setter(Type entity, string propertyName)
    {
        var property = entity.GetProperty(propertyName);

        property.ShouldNotBeNull();
        (property.SetMethod?.IsPublic ?? false).ShouldBeFalse(
            $"{entity.Name}.{propertyName} must not be publicly settable (TEN-018).");
    }

    /// <summary>
    /// Every append-only entity is free of a concurrency token, because it is never updated
    /// (TZ 11.6). A token appearing here would signal that someone intends to update the row.
    /// </summary>
    [Theory]
    [InlineData(typeof(UserBranchHistory))]
    [InlineData(typeof(UserCategoryHistory))]
    public void Append_only_entities_carry_no_updated_at(Type entity)
    {
        entity.GetProperty("UpdatedAt").ShouldBeNull(
            $"{entity.Name} is append-only and must not track modification (USER-026, BRN-025).");
    }

    /// <summary>
    /// The domain owns the invariants, so it must not depend on the error catalog of the contracts
    /// layer; <c>DomainErrorCodeTests</c> keeps the duplicated literals in sync instead.
    /// </summary>
    [Fact]
    public void Domain_carries_its_own_error_codes()
    {
        Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOn("MentorTaskFlow.Contracts")
            .GetResult()
            .ShouldPass("Domain must not reference Contracts.");
    }
}
