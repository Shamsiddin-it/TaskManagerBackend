using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using Npgsql;

namespace MentorTaskFlow.IntegrationTests.Persistence;

/// <summary>
/// Proves that tenant isolation holds at the database level, independently of application code.
/// </summary>
/// <remarks>
/// Covers <c>TEST-TEN-031</c>, <c>TEST-TEN-032</c>, <c>TEST-DB-010</c> and the part of
/// <c>TEST-TEN-040</c> whose composite FKs exist after Phase 1. Every statement here is raw SQL: the
/// point is that a defect which bypasses the domain, the handlers and the query filters is still
/// refused by PostgreSQL (<c>TEN-023</c>, <c>TEN-099</c>).
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class TenantDatabaseConstraintTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private Guid _orgA;
    private Guid _orgB;
    private Guid _headOfficeA;
    private Guid _khujandA;
    private Guid _headOfficeB;
    private Guid _categoryHeadOfficeA;
    private Guid _categoryKhujandA;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();

        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        // The fixture of TZ 31.9: «SoftClub Academy» with a head office and the Khujand branch, each
        // holding a `C#` category, plus a second organization for level-1 checks.
        var organizationA = Organization.Provision("SoftClub Academy", "softclub-academy", Now);
        var organizationB = Organization.Provision("Other Academy", "other-academy", Now);
        context.Organizations.AddRange(organizationA, organizationB);

        var headOfficeA = Branch.CreateHeadOffice(organizationA.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Now);
        var khujandA = Branch.Create(organizationA.Id, "Филиал Худжанд", "KHJ", null, "Asia/Dushanbe", Now);
        var headOfficeB = Branch.CreateHeadOffice(organizationB.Id, "Головной офис", "HQ", null, "Asia/Dushanbe", Now);
        context.Branches.AddRange(headOfficeA, khujandA, headOfficeB);

        var categoryHeadOffice = Category.Create(organizationA.Id, headOfficeA.Id, "C#", null, Now);
        var categoryKhujand = Category.Create(organizationA.Id, khujandA.Id, "C#", null, Now);
        context.Categories.AddRange(categoryHeadOffice, categoryKhujand);

        context.CategorySettings.AddRange(
            CategorySettings.CreateDefault(categoryHeadOffice, headOfficeA.TimeZoneId, Now),
            CategorySettings.CreateDefault(categoryKhujand, khujandA.TimeZoneId, Now));

        await context.SaveChangesAsync();

        _orgA = organizationA.Id;
        _orgB = organizationB.Id;
        _headOfficeA = headOfficeA.Id;
        _khujandA = khujandA.Id;
        _headOfficeB = headOfficeB.Id;
        _categoryHeadOfficeA = categoryHeadOffice.Id;
        _categoryKhujandA = categoryKhujand.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------------
    // TEST-TEN-031 — category name uniqueness is per branch, not global
    // ---------------------------------------------------------------------

    /// <summary>
    /// The same name in two branches is legitimate and must coexist: `C#` in the head office and `C#`
    /// in Khujand are different entities with no shared data (<c>CAT-021</c>).
    /// </summary>
    [Fact]
    public void Same_category_name_coexists_in_two_branches()
    {
        _categoryHeadOfficeA.ShouldNotBe(_categoryKhujandA);
    }

    [Fact]
    public async Task Duplicate_category_name_within_one_branch_is_refused()
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            InsertCategoryAsync(Guid.CreateVersion7(), _orgA, _headOfficeA, "C#"));

        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        exception.ConstraintName.ShouldBe("ux_categories_branch_normalized_name");
    }

    // ---------------------------------------------------------------------
    // TEST-TEN-032 — exactly one head office per organization
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_second_head_office_in_one_organization_is_refused()
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            SetHeadOfficeAsync(_khujandA));

        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        exception.ConstraintName.ShouldBe("ux_branches_single_head_office");
    }

    /// <summary>The index is partial, so a head office per organization stays possible.</summary>
    [Fact]
    public async Task Each_organization_keeps_its_own_head_office()
    {
        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM branches WHERE is_head_office = true";

        (await command.ExecuteScalarAsync()).ShouldBe(2L);
    }

    // ---------------------------------------------------------------------
    // TEST-DB-010 — ck_users_scope_shape
    // ---------------------------------------------------------------------

    public static TheoryData<string, string?, bool, bool> InvalidUserShapes() => new()
    {
        // role, admin_scope, has branch, has category
        { "Admin", null, false, false },
        { "Admin", "Organization", true, false },
        { "Admin", "Branch", false, false },
        { "Admin", "Branch", true, true },
        { "Lead", "Branch", true, true },
        { "Lead", null, false, true },
        { "Mentor", null, true, false },
    };

    [Theory]
    [MemberData(nameof(InvalidUserShapes))]
    public async Task Invalid_user_scope_shapes_are_refused_by_the_database(
        string role,
        string? adminScope,
        bool hasBranch,
        bool hasCategory)
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            InsertUserAsync(
                _orgA,
                hasBranch ? _headOfficeA : null,
                hasCategory ? _categoryHeadOfficeA : null,
                role,
                adminScope));

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        exception.ConstraintName.ShouldBeOneOf(
            "ck_users_scope_shape",
            "ck_users_role_admin_scope",
            "ck_users_role_category");
    }

    [Fact]
    public async Task The_four_valid_user_shapes_are_accepted()
    {
        await InsertUserAsync(_orgA, null, null, "Admin", "Organization");
        await InsertUserAsync(_orgA, _headOfficeA, null, "Admin", "Branch");
        await InsertUserAsync(_orgA, _headOfficeA, _categoryHeadOfficeA, "Lead", null);
        await InsertUserAsync(_orgA, _headOfficeA, _categoryHeadOfficeA, "Mentor", null);
    }

    // ---------------------------------------------------------------------
    // TEST-TEN-040 — composite FKs make cross-scope rows impossible
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>fk_users_branch_scope</c>: a user of organization A placed in a branch of organization B.
    /// </summary>
    [Fact]
    public async Task A_user_cannot_be_placed_in_a_branch_of_a_foreign_organization()
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            InsertUserAsync(_orgA, _headOfficeB, null, "Admin", "Branch"));

        exception.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        exception.ConstraintName.ShouldBe("fk_users_branch_scope");
    }

    /// <summary>
    /// <c>fk_users_category_scope</c>, <c>USER-024</c>: «a Lead of branch A attached to a category of
    /// branch B» — the exact mixing that motivated the whole tenancy model.
    /// </summary>
    [Fact]
    public async Task A_lead_cannot_be_attached_to_a_category_of_another_branch()
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            InsertUserAsync(_orgA, _headOfficeA, _categoryKhujandA, "Lead", null));

        exception.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        exception.ConstraintName.ShouldBe("fk_users_category_scope");
    }

    /// <summary><c>fk_categories_branch_scope</c>: a category in a branch of a foreign organization.</summary>
    [Fact]
    public async Task A_category_cannot_live_in_a_branch_of_a_foreign_organization()
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            InsertCategoryAsync(Guid.CreateVersion7(), _orgA, _headOfficeB, "Python"));

        exception.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        exception.ConstraintName.ShouldBe("fk_categories_branch_scope");
    }

    /// <summary><c>fk_category_settings_scope</c>: settings whose scope differs from their category.</summary>
    [Fact]
    public async Task Category_settings_cannot_carry_a_foreign_scope()
    {
        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO category_settings
                (category_id, organization_id, branch_id, time_zone_id, default_assignment_due_days,
                 default_due_time_local, deadline_reminder_hours, allow_late_submission, updated_at)
            VALUES (@categoryId, @organizationId, @branchId, 'Asia/Dushanbe', 3,
                    '23:59', 24, true, now());
            """;
        command.Parameters.AddWithValue("categoryId", _categoryHeadOfficeA);
        command.Parameters.AddWithValue("organizationId", _orgA);
        // The category belongs to the head office; claim it belongs to Khujand.
        command.Parameters.AddWithValue("branchId", _khujandA);

        var exception = await Should.ThrowAsync<PostgresException>(command.ExecuteNonQueryAsync());

        exception.SqlState.ShouldBeOneOf(
            PostgresErrorCodes.ForeignKeyViolation,
            PostgresErrorCodes.UniqueViolation);
    }

    /// <summary>
    /// <c>ck_user_branch_history_change</c>: a «transfer to the same branch» row must not exist. The
    /// constraint uses <c>IS DISTINCT FROM</c>, so it also catches the NULL–NULL case that <c>&lt;&gt;</c>
    /// would silently allow.
    /// </summary>
    [Fact]
    public async Task Branch_history_rejects_a_transfer_to_the_same_branch()
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            InsertBranchHistoryAsync(_orgA, _khujandA, _khujandA));

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        exception.ConstraintName.ShouldBe("ck_user_branch_history_change");
    }

    [Fact]
    public async Task Branch_history_rejects_a_null_to_null_transfer()
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            InsertBranchHistoryAsync(_orgA, null, null));

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        exception.ConstraintName.ShouldBe("ck_user_branch_history_change");
    }

    // ---------------------------------------------------------------------
    // DEPLOY-007 / TEN-021 — no cascades anywhere
    // ---------------------------------------------------------------------

    /// <summary>
    /// Deleting an organization that still has branches must fail. Cascade deletion is absent from
    /// the schema entirely; domain entities are deactivated, never removed (<c>DEPLOY-007</c>).
    /// </summary>
    [Fact]
    public async Task Deleting_a_referenced_organization_is_refused()
    {
        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM organizations WHERE id = @id";
        command.Parameters.AddWithValue("id", _orgA);

        var exception = await Should.ThrowAsync<PostgresException>(command.ExecuteNonQueryAsync());

        exception.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Every_foreign_key_in_the_schema_uses_restrict()
    {
        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();

        // confdeltype: 'a' = NO ACTION, 'r' = RESTRICT, 'c' = CASCADE, 'n' = SET NULL, 'd' = SET DEFAULT.
        // Anything other than NO ACTION or RESTRICT is a cascade path and a defect.
        command.CommandText = """
            SELECT count(*)
            FROM pg_constraint
            WHERE contype = 'f' AND confdeltype NOT IN ('a', 'r');
            """;

        (await command.ExecuteScalarAsync()).ShouldBe(0L);
    }

    // ---------------------------------------------------------------------
    // Raw SQL helpers — deliberately bypassing EF Core
    // ---------------------------------------------------------------------

    private async Task InsertCategoryAsync(Guid id, Guid organizationId, Guid branchId, string name)
    {
        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO categories
                (id, organization_id, branch_id, name, normalized_name, is_active, created_at, updated_at)
            VALUES (@id, @organizationId, @branchId, @name, @normalizedName, true, now(), now());
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("branchId", branchId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("normalizedName", name.ToUpperInvariant());

        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertUserAsync(
        Guid organizationId,
        Guid? branchId,
        Guid? categoryId,
        string role,
        string? adminScope)
    {
        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users
                (id, full_name, email, normalized_email, role, admin_scope, organization_id,
                 branch_id, category_id, token_version, is_active, failed_login_count,
                 created_at, updated_at)
            VALUES (@id, 'Тестовый пользователь', @email, @normalizedEmail, @role, @adminScope,
                    @organizationId, @branchId, @categoryId, 0, true, 0, now(), now());
            """;

        var email = $"{Guid.CreateVersion7():N}@mtf.test";
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("normalizedEmail", email.ToUpperInvariant());
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("adminScope", (object?)adminScope ?? DBNull.Value);
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("branchId", (object?)branchId ?? DBNull.Value);
        command.Parameters.AddWithValue("categoryId", (object?)categoryId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertBranchHistoryAsync(Guid organizationId, Guid? oldBranchId, Guid? newBranchId)
    {
        var userId = Guid.CreateVersion7();

        await using var seed = fixture.CreateContext(suppressTenantFilter: true);
        seed.Users.Add(User.CreateOrganizationAdmin(organizationId, "Админ", $"{userId:N}@mtf.test", Now));
        await seed.SaveChangesAsync();
        var actor = seed.Users.OrderBy(u => u.CreatedAt).First();

        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_branch_history
                (id, organization_id, user_id, old_branch_id, new_branch_id, changed_by_id,
                 reason, changed_at, correlation_id, created_at)
            VALUES (@id, @organizationId, @userId, @oldBranchId, @newBranchId, @changedById,
                    'Перевод по заявлению', now(), @correlationId, now());
            """;
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("organizationId", organizationId);
        command.Parameters.AddWithValue("userId", actor.Id);
        command.Parameters.AddWithValue("oldBranchId", (object?)oldBranchId ?? DBNull.Value);
        command.Parameters.AddWithValue("newBranchId", (object?)newBranchId ?? DBNull.Value);
        command.Parameters.AddWithValue("changedById", actor.Id);
        command.Parameters.AddWithValue("correlationId", Guid.CreateVersion7());

        await command.ExecuteNonQueryAsync();
    }

    private async Task SetHeadOfficeAsync(Guid branchId)
    {
        await using var connection = await fixture.OpenRawConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE branches SET is_head_office = true WHERE id = @id";
        command.Parameters.AddWithValue("id", branchId);

        await command.ExecuteNonQueryAsync();
    }
}
