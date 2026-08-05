using MentorTaskFlow.Domain.Common;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;

namespace MentorTaskFlow.UnitTests.Tenancy;

/// <summary>
/// <c>USER-023</c>: the scope fields admit exactly four combinations.
/// </summary>
/// <remarks>
/// This is the application-side half of the guarantee; the database half is
/// <c>ck_users_scope_shape</c>, verified against a real PostgreSQL by
/// <c>TenantDatabaseConstraintTests</c>. Both must exist — application validation is never the only
/// protection for an invariant that can be expressed as a constraint (<c>TEN-023</c>).
/// </remarks>
public sealed class UserScopeShapeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid BranchId = Guid.CreateVersion7();
    private static readonly Guid CategoryId = Guid.CreateVersion7();

    [Fact]
    public void Organization_admin_has_no_branch_and_no_category()
    {
        var user = User.CreateOrganizationAdmin(Org, "Иван Каримов", "oa@mtf.test", Now);

        user.Role.ShouldBe(UserRole.Admin);
        user.AdminScope.ShouldBe(AdminScope.Organization);
        user.OrganizationId.ShouldBe(Org);
        user.BranchId.ShouldBeNull();
        user.CategoryId.ShouldBeNull();
    }

    [Fact]
    public void Branch_admin_has_a_branch_but_no_category()
    {
        var user = User.CreateBranchAdmin(Org, BranchId, "Дилшод Рахимов", "ba@mtf.test", Now);

        user.AdminScope.ShouldBe(AdminScope.Branch);
        user.BranchId.ShouldBe(BranchId);
        user.CategoryId.ShouldBeNull();
    }

    [Theory]
    [InlineData(UserRole.Lead)]
    [InlineData(UserRole.Mentor)]
    public void Lead_and_mentor_have_branch_category_and_no_admin_scope(UserRole role)
    {
        var user = role is UserRole.Lead
            ? User.CreateLead(Org, BranchId, CategoryId, "Лид", "lead@mtf.test", Now)
            : User.CreateMentor(Org, BranchId, CategoryId, "Ментор", "mentor@mtf.test", Now);

        user.Role.ShouldBe(role);
        user.AdminScope.ShouldBeNull();
        user.BranchId.ShouldBe(BranchId);
        user.CategoryId.ShouldBe(CategoryId);
    }

    public static TheoryData<UserRole, AdminScope?, Guid?, Guid?> InvalidShapes() => new()
    {
        // Admin without a scope — TEN-013 makes AdminScope mandatory for Admin.
        { UserRole.Admin, null, null, null },
        // Organization Admin pinned to a branch: would be unable to create a second branch.
        { UserRole.Admin, AdminScope.Organization, BranchId, null },
        // Any Admin with a category — Admin never participates in the study cycle.
        { UserRole.Admin, AdminScope.Branch, BranchId, CategoryId },
        // Branch Admin without a branch: its whole contour is one branch.
        { UserRole.Admin, AdminScope.Branch, null, null },
        // Lead/Mentor carrying an admin scope.
        { UserRole.Lead, AdminScope.Branch, BranchId, CategoryId },
        // Lead/Mentor without a branch or without a category.
        { UserRole.Lead, null, null, CategoryId },
        { UserRole.Mentor, null, BranchId, null },
    };

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public void Every_other_combination_is_rejected(UserRole role, AdminScope? adminScope, Guid? branchId, Guid? categoryId)
    {
        var exception = Should.Throw<DomainException>(
            () => User.EnsureScopeShape(role, adminScope, branchId, categoryId));

        exception.Code.ShouldBe(DomainErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Organization_id_is_required_for_every_role()
    {
        Should.Throw<DomainException>(
            () => User.CreateOrganizationAdmin(Guid.Empty, "Иван", "a@mtf.test", Now));
    }

    /// <summary>
    /// <c>TEN-028</c>: normalization must be culture-invariant, otherwise the same address maps to
    /// different values depending on the host locale and the uniqueness index stops holding.
    /// </summary>
    [Fact]
    public void Normalized_email_is_culture_invariant_uppercase()
    {
        var user = User.CreateMentor(Org, BranchId, CategoryId, "Ментор", "Ivan.Karimov@MTF.test", Now);

        user.Email.ShouldBe("Ivan.Karimov@MTF.test");
        user.NormalizedEmail.ShouldBe("IVAN.KARIMOV@MTF.TEST");
    }

    /// <summary>Any change of scope or access level must invalidate issued tokens (<c>AUTH-034</c>).</summary>
    [Fact]
    public void Scope_changing_operations_bump_the_token_version()
    {
        var user = User.CreateMentor(Org, BranchId, CategoryId, "Ментор", "m@mtf.test", Now);
        var newCategory = Guid.CreateVersion7();
        var newBranch = Guid.CreateVersion7();

        user.TokenVersion.ShouldBe(0);

        user.ChangeCategory(newCategory, Now);
        user.TokenVersion.ShouldBe(1);

        user.ChangeBranch(newBranch, newCategory, Now);
        user.TokenVersion.ShouldBe(2);

        user.Deactivate(Now);
        user.TokenVersion.ShouldBe(3);
    }

    [Fact]
    public void Changing_branch_into_an_invalid_shape_is_rejected()
    {
        var lead = User.CreateLead(Org, BranchId, CategoryId, "Лид", "l@mtf.test", Now);

        // A Lead must always keep a category; moving branches without one is not a valid shape.
        Should.Throw<DomainException>(() => lead.ChangeBranch(Guid.CreateVersion7(), null, Now));
    }
}
