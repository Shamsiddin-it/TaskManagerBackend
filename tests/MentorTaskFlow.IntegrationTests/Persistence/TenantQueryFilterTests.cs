using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.IntegrationTests.Persistence;

/// <summary>
/// Levels 1 and 2 of the isolation model as applied by the EF Core global query filters (TZ 9.1).
/// </summary>
/// <remarks>
/// These filters are necessary but never sufficient: <c>SEC-002</c> and <c>SEC-030</c> still require
/// an explicit scope condition in every handler, because a filter is dropped by
/// <c>IgnoreQueryFilters()</c>, does not apply to raw SQL and does not guard writes. What is tested
/// here is that the ORM layer defaults to closed rather than open.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class TenantQueryFilterTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private Guid _orgA;
    private Guid _orgB;
    private Guid _headOfficeA;
    private Guid _khujandA;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();

        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var organizationA = Organization.Provision("SoftClub Academy", "softclub-academy", Now);
        var organizationB = Organization.Provision("Other Academy", "other-academy", Now);
        context.Organizations.AddRange(organizationA, organizationB);

        var headOfficeA = Branch.CreateHeadOffice(organizationA.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Now);
        var khujandA = Branch.Create(organizationA.Id, "Филиал Худжанд", "KHJ", null, "Asia/Dushanbe", Now);
        var headOfficeB = Branch.CreateHeadOffice(organizationB.Id, "Головной офис", "HQ", null, "Asia/Dushanbe", Now);
        context.Branches.AddRange(headOfficeA, khujandA, headOfficeB);

        context.Categories.AddRange(
            Category.Create(organizationA.Id, headOfficeA.Id, "C#", null, Now),
            Category.Create(organizationA.Id, khujandA.Id, "C#", null, Now),
            Category.Create(organizationB.Id, headOfficeB.Id, "C#", null, Now));

        await context.SaveChangesAsync();

        _orgA = organizationA.Id;
        _orgB = organizationB.Id;
        _headOfficeA = headOfficeA.Id;
        _khujandA = khujandA.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The fail-closed default. A request that never established a scope must see nothing — the
    /// opposite default would turn a forgotten middleware registration into a cross-tenant leak.
    /// </summary>
    [Fact]
    public async Task Without_a_scope_nothing_is_visible()
    {
        await using var context = fixture.CreateContext();

        (await context.Categories.CountAsync()).ShouldBe(0);
        (await context.Branches.CountAsync()).ShouldBe(0);
        (await context.Organizations.CountAsync()).ShouldBe(0);
    }

    /// <summary><c>TEST-TEN-002</c>: a list for organization A contains no row of organization B.</summary>
    [Fact]
    public async Task Organization_scope_hides_every_other_organization()
    {
        await using var context = fixture.CreateContext(organizationId: _orgA);

        var categories = await context.Categories.ToListAsync();

        categories.Count.ShouldBe(2);
        categories.ShouldAllBe(c => c.OrganizationId == _orgA);
    }

    /// <summary><c>TEST-TEN-003</c>: the counter must not disclose hidden rows either.</summary>
    [Fact]
    public async Task Total_count_is_computed_under_the_same_filter()
    {
        await using var context = fixture.CreateContext(organizationId: _orgA);

        (await context.Categories.CountAsync()).ShouldBe(2);
        (await context.Branches.CountAsync()).ShouldBe(2);
    }

    /// <summary><c>ORG-022</c>: a user never learns that another organization exists.</summary>
    [Fact]
    public async Task Only_the_callers_own_organization_is_visible()
    {
        await using var context = fixture.CreateContext(organizationId: _orgA);

        var organizations = await context.Organizations.ToListAsync();

        organizations.ShouldHaveSingleItem().Id.ShouldBe(_orgA);
        (await context.Organizations.AnyAsync(o => o.Id == _orgB)).ShouldBeFalse();
    }

    /// <summary>
    /// <c>TEST-TEN-008</c>: with a branch selected, the categories of a sibling branch disappear —
    /// including the one carrying the identical name.
    /// </summary>
    [Fact]
    public async Task Branch_scope_hides_the_sibling_branch()
    {
        await using var context = fixture.CreateContext(organizationId: _orgA, branchId: _headOfficeA);

        var categories = await context.Categories.ToListAsync();

        categories.ShouldHaveSingleItem().BranchId.ShouldBe(_headOfficeA);
    }

    /// <summary>
    /// <c>TEST-TEN-010</c>: in the all-branches read context an Organization Admin sees both `C#`
    /// categories as <b>separate</b> rows with different ids and different branches. Aggregating them
    /// by name would merge unrelated study streams and is a Critical defect (<c>TEN-071</c>).
    /// </summary>
    [Fact]
    public async Task All_branches_context_returns_same_named_categories_as_distinct_rows()
    {
        await using var context = fixture.CreateContext(organizationId: _orgA, branchId: null);

        var categories = await context.Categories.Where(c => c.Name == "C#").ToListAsync();

        categories.Count.ShouldBe(2);
        categories.Select(c => c.Id).Distinct().Count().ShouldBe(2);
        categories.Select(c => c.BranchId).ShouldBe(new[] { _headOfficeA, _khujandA }, ignoreOrder: true);
    }

    /// <summary>
    /// A scope belonging to organization A cannot reach a row of organization B even by primary key —
    /// which is what turns into 404 <c>RESOURCE_NOT_FOUND</c> at the API boundary
    /// (<c>TEST-TEN-001</c>, <c>TEST-TEN-011</c>).
    /// </summary>
    [Fact]
    public async Task A_foreign_row_is_unreachable_by_direct_id()
    {
        Guid foreignCategoryId;
        await using (var seed = fixture.CreateContext(suppressTenantFilter: true))
        {
            foreignCategoryId = (await seed.Categories.FirstAsync(c => c.OrganizationId == _orgB)).Id;
        }

        await using var context = fixture.CreateContext(organizationId: _orgA);

        (await context.Categories.FindAsync(foreignCategoryId)).ShouldBeNull();
        (await context.Categories.FirstOrDefaultAsync(c => c.Id == foreignCategoryId)).ShouldBeNull();
    }
}
