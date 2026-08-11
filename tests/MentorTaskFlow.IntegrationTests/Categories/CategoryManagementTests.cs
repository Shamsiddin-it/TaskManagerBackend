using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Categories;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.IntegrationTests.Categories;

/// <summary>Categories and their settings (Приложение D.3, TZ 15.3, 39.4).</summary>
[Collection(PostgresCollection.Name)]
public sealed class CategoryManagementTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string BranchHeader = "X-MTF-Branch-Id";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _headOfficeId;
    private Guid _khujandId;
    private Guid _headCategoryId;
    private Guid _khujandCategoryId;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await SeedAsync();

        _factory = new MentorTaskFlowApiFactory { ConnectionStringOverride = fixture.ConnectionString };
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------
    // Visibility (CAT-007)
    // -----------------------------------------------------------------

    /// <summary>
    /// <c>TEST-TEN-010</c>: the two `C#` categories are separate rows with different ids and
    /// different branches. Merging them by name would fuse unrelated study streams (<c>TEN-071</c>).
    /// </summary>
    [Fact]
    public async Task An_organization_admin_sees_both_same_named_categories_as_distinct_rows()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<CategoryDto>>(await client.GetAsync("/api/v1/categories"));

        var csharp = page.Items.Where(c => c.Name == "C#").ToArray();
        csharp.Length.ShouldBe(2);
        csharp.Select(c => c.Id).Distinct().Count().ShouldBe(2);
        csharp.Select(c => c.BranchId).ShouldBe([_headOfficeId, _khujandId], ignoreOrder: true);

        // TEN-073: every row in the all-branches context names its branch, or the two are
        // indistinguishable to a reader.
        csharp.ShouldAllBe(c => c.Branch != null);
    }

    [Fact]
    public async Task Selecting_a_branch_narrows_the_list_to_it()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        client.DefaultRequestHeaders.Add(BranchHeader, _khujandId.ToString());

        var page = await ReadAsync<PagedResult<CategoryDto>>(await client.GetAsync("/api/v1/categories"));

        page.Items.ShouldHaveSingleItem().BranchId.ShouldBe(_khujandId);
    }

    [Fact]
    public async Task A_branch_admin_sees_only_their_own_branch()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var page = await ReadAsync<PagedResult<CategoryDto>>(await client.GetAsync("/api/v1/categories"));

        page.Items.ShouldAllBe(c => c.BranchId == _headOfficeId);
    }

    /// <summary><c>TEST-TEN-008</c>: a Lead sees exactly one category — their own.</summary>
    [Theory]
    [InlineData("lead-head@mentortaskflow.test")]
    [InlineData("mentor-head@mentortaskflow.test")]
    public async Task Lead_and_mentor_see_exactly_their_own_category(string email)
    {
        using var client = await SignInAsync(email);

        var page = await ReadAsync<PagedResult<CategoryDto>>(await client.GetAsync("/api/v1/categories"));

        page.Items.ShouldHaveSingleItem().Id.ShouldBe(_headCategoryId);
        page.TotalCount.ShouldBe(1);
    }

    /// <summary>
    /// The `C#` category of the sibling branch answers 404 — identical to one that does not exist
    /// (<c>TEST-TEN-008</c>, <c>TEN-006</c>).
    /// </summary>
    [Theory]
    [InlineData("lead-head@mentortaskflow.test")]
    [InlineData("branch-admin-head@mentortaskflow.test")]
    public async Task A_category_of_another_branch_answers_not_found(string email)
    {
        using var client = await SignInAsync(email);

        var response = await client.GetAsync($"/api/v1/categories/{_khujandCategoryId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ResourceNotFound);
    }

    // -----------------------------------------------------------------
    // Creation (CAT-001, CAT-014, CAT-021, CAT-025)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_branch_admin_creates_in_their_own_branch_and_gets_default_settings()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/categories",
            new CreateCategoryRequest("Python", "Backend на Python"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var category = await ReadAsync<CategoryDto>(response);
        category.BranchId.ShouldBe(_headOfficeId);
        category.IsActive.ShouldBeTrue();

        // CAT-014: a category without settings does not exist. CAT-023: the time zone is inherited
        // from the branch.
        var settings = await ReadAsync<CategorySettingsDto>(
            await client.GetAsync($"/api/v1/categories/{category.Id}/settings"));

        settings.TimeZoneId.ShouldBe("Asia/Dushanbe");
        settings.DefaultAssignmentDueDays.ShouldBe(3);
        settings.DefaultDueTimeLocal.ShouldBe(new TimeOnly(23, 59));
        settings.DeadlineReminderHours.ShouldBe(24);
        settings.AllowLateSubmission.ShouldBeTrue();
    }

    /// <summary>
    /// <c>TEN-033</c>: a branch-scoped mutation by an Organization Admin needs a chosen branch. There
    /// is no default to assume, because no endpoint changes more than one branch per request.
    /// </summary>
    [Fact]
    public async Task An_organization_admin_must_choose_a_branch_to_create()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/categories",
            new CreateCategoryRequest("Python", null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.BranchContextRequired);
    }

    [Fact]
    public async Task An_organization_admin_creates_in_the_selected_branch()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        client.DefaultRequestHeaders.Add(BranchHeader, _khujandId.ToString());

        var response = await client.PostAsJsonAsync("/api/v1/categories",
            new CreateCategoryRequest("Python", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReadAsync<CategoryDto>(response)).BranchId.ShouldBe(_khujandId);
    }

    /// <summary>
    /// <c>CAT-021</c>: uniqueness is per branch, not global. Version 2.1's global constraint is
    /// cancelled precisely so `C#` can exist in two branches as two entities.
    /// </summary>
    [Fact]
    public async Task The_same_name_is_free_in_another_branch_but_taken_in_this_one()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        client.DefaultRequestHeaders.Add(BranchHeader, _headOfficeId.ToString());

        var duplicate = await client.PostAsJsonAsync("/api/v1/categories", new CreateCategoryRequest("C#", null));
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(duplicate)).ShouldBe(ErrorCodes.ResourceAlreadyExists);

        // Case-insensitive: NormalizedName is what the index uses.
        var lowercase = await client.PostAsJsonAsync("/api/v1/categories", new CreateCategoryRequest("c#", null));
        lowercase.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    /// <summary><c>SEC-003</c>: scope is never accepted from the client.</summary>
    [Fact]
    public async Task A_request_carrying_branch_id_is_refused()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var response = await client.PostAsync("/api/v1/categories", JsonContent.Create(new
        {
            name = "Python",
            branchId = Guid.CreateVersion7(),
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    /// <summary><c>TEN-032</c>: the header is refused for a Branch Admin even naming their own branch.</summary>
    [Fact]
    public async Task A_branch_admin_sending_the_scope_header_is_refused()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");
        client.DefaultRequestHeaders.Add(BranchHeader, _headOfficeId.ToString());

        var response = await client.PostAsJsonAsync("/api/v1/categories", new CreateCategoryRequest("Python", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ScopeOverrideForbidden);
    }

    [Theory]
    [InlineData("lead-head@mentortaskflow.test")]
    [InlineData("mentor-head@mentortaskflow.test")]
    public async Task Lead_and_mentor_cannot_create_a_category(string email)
    {
        using var client = await SignInAsync(email);

        var response = await client.PostAsJsonAsync("/api/v1/categories", new CreateCategoryRequest("Python", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Deactivation (CAT-003, CAT-010, CAT-011)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Deactivating_a_staffed_category_needs_confirmation_and_keeps_its_users()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var category = await ReadAsync<CategoryDto>(await client.GetAsync($"/api/v1/categories/{_headCategoryId}"));

        var refused = await client.PostAsJsonAsync($"/api/v1/categories/{_headCategoryId}/deactivate",
            new DeactivateCategoryRequest(category.ConcurrencyToken));

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(refused)).ShouldBe(ErrorCodes.CategoryHasActiveUsers);

        var confirmed = await client.PostAsJsonAsync($"/api/v1/categories/{_headCategoryId}/deactivate",
            new DeactivateCategoryRequest(category.ConcurrencyToken, ConfirmActiveUsers: true));

        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<CategoryDto>(confirmed)).IsActive.ShouldBeFalse();

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        (await context.Users.CountAsync(u => u.CategoryId == _headCategoryId && u.IsActive))
            .ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// <c>CAT-010</c>: writes in the deactivated category's contour are refused, while historical
    /// reads stay available (<c>CAT-012</c>).
    /// </summary>
    [Fact]
    public async Task A_deactivated_category_refuses_writes_but_still_reads()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var category = await ReadAsync<CategoryDto>(await client.GetAsync($"/api/v1/categories/{_headCategoryId}"));

        var deactivated = await ReadAsync<CategoryDto>(await client.PostAsJsonAsync(
            $"/api/v1/categories/{_headCategoryId}/deactivate",
            new DeactivateCategoryRequest(category.ConcurrencyToken, ConfirmActiveUsers: true)));

        var write = await client.PutAsJsonAsync($"/api/v1/categories/{_headCategoryId}",
            new UpdateCategoryRequest("C# Advanced", null, deactivated.ConcurrencyToken));

        write.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(write)).ShouldBe(ErrorCodes.CategoryInactive);

        (await client.GetAsync($"/api/v1/categories/{_headCategoryId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_deactivated_category_can_be_activated_again()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var category = await ReadAsync<CategoryDto>(await client.GetAsync($"/api/v1/categories/{_headCategoryId}"));

        var deactivated = await ReadAsync<CategoryDto>(await client.PostAsJsonAsync(
            $"/api/v1/categories/{_headCategoryId}/deactivate",
            new DeactivateCategoryRequest(category.ConcurrencyToken, ConfirmActiveUsers: true)));

        var activated = await client.PostAsJsonAsync($"/api/v1/categories/{_headCategoryId}/activate",
            new CategoryActionRequest(deactivated.ConcurrencyToken));

        activated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<CategoryDto>(activated)).IsActive.ShouldBeTrue();
    }

    /// <summary>
    /// <c>TEN-008</c> and <c>BRN-032</c>: a deactivated branch outranks the category. Reporting the
    /// category would send an administrator to fix the wrong thing.
    /// </summary>
    [Fact]
    public async Task A_deactivated_branch_outranks_the_category_in_the_error()
    {
        using var organizationAdmin = await SignInAsync("organization-admin@mentortaskflow.test");

        var khujand = await ReadAsync<Contracts.Tenancy.BranchDto>(
            await organizationAdmin.GetAsync($"/api/v1/branches/{_khujandId}"));

        await organizationAdmin.PostAsJsonAsync($"/api/v1/branches/{_khujandId}/deactivate",
            new Contracts.Tenancy.DeactivateBranchRequest(khujand.ConcurrencyToken, ConfirmActiveUsers: true));

        organizationAdmin.DefaultRequestHeaders.Add(BranchHeader, _khujandId.ToString());

        var category = await ReadAsync<CategoryDto>(
            await organizationAdmin.GetAsync($"/api/v1/categories/{_khujandCategoryId}"));

        var write = await organizationAdmin.PutAsJsonAsync($"/api/v1/categories/{_khujandCategoryId}",
            new UpdateCategoryRequest("C# Advanced", null, category.ConcurrencyToken));

        write.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(write)).ShouldBe(ErrorCodes.BranchInactive);
    }

    // -----------------------------------------------------------------
    // Settings (CAT-004, CAT-005, CAT-006)
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("lead-head@mentortaskflow.test")]
    [InlineData("mentor-head@mentortaskflow.test")]
    public async Task Lead_and_mentor_may_read_their_own_settings(string email)
    {
        using var client = await SignInAsync(email);

        var response = await client.GetAsync($"/api/v1/categories/{_headCategoryId}/settings");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<CategorySettingsDto>(response)).TimeZoneId.ShouldBe("Asia/Dushanbe");
    }

    [Fact]
    public async Task Settings_of_another_category_answer_not_found()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        (await client.GetAsync($"/api/v1/categories/{_khujandCategoryId}/settings"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lead_cannot_change_settings()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await client.PutAsJsonAsync($"/api/v1/categories/{_headCategoryId}/settings",
            new UpdateCategorySettingsRequest("Asia/Dushanbe", 5, new TimeOnly(18, 0), 12, false, "irrelevant"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_admin_updates_settings_and_the_time_zone_change_is_audited()
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var settings = await ReadAsync<CategorySettingsDto>(
            await client.GetAsync($"/api/v1/categories/{_headCategoryId}/settings"));

        var response = await client.PutAsJsonAsync($"/api/v1/categories/{_headCategoryId}/settings",
            new UpdateCategorySettingsRequest("Europe/Moscow", 5, new TimeOnly(18, 0), 12, false, settings.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await ReadAsync<CategorySettingsDto>(response);
        updated.TimeZoneId.ShouldBe("Europe/Moscow");
        updated.DefaultAssignmentDueDays.ShouldBe(5);
        updated.AllowLateSubmission.ShouldBeFalse();

        // CAT-005: both values are recorded, because the time zone decides every future deadline and
        // the scheduler's firing time. Without the old value an incident review cannot explain why
        // deadlines moved.
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var audit = await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.CategorySettingsUpdate);

        audit.Metadata!.RootElement.GetProperty("previousTimeZoneId").GetString().ShouldBe("Asia/Dushanbe");
        audit.Metadata.RootElement.GetProperty("newTimeZoneId").GetString().ShouldBe("Europe/Moscow");
        audit.BranchId.ShouldBe(_headOfficeId);
        audit.CategoryId.ShouldBe(_headCategoryId);
    }

    [Theory]
    [InlineData("Asia/Atlantis")]
    [InlineData("Central Asia Standard Time")]
    public async Task An_unknown_time_zone_is_refused(string timeZoneId)
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var settings = await ReadAsync<CategorySettingsDto>(
            await client.GetAsync($"/api/v1/categories/{_headCategoryId}/settings"));

        var response = await client.PutAsJsonAsync($"/api/v1/categories/{_headCategoryId}/settings",
            new UpdateCategorySettingsRequest(timeZoneId, 3, new TimeOnly(23, 59), 24, true, settings.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData(0, 24)]    // due days below the range
    [InlineData(61, 24)]   // due days above it
    [InlineData(3, 0)]     // reminder hours: 0 is rejected, disabling reminders is unsupported
    [InlineData(3, 169)]
    public async Task Settings_outside_the_permitted_ranges_are_refused(int dueDays, int reminderHours)
    {
        using var client = await SignInAsync("branch-admin-head@mentortaskflow.test");
        var settings = await ReadAsync<CategorySettingsDto>(
            await client.GetAsync($"/api/v1/categories/{_headCategoryId}/settings"));

        var response = await client.PutAsJsonAsync($"/api/v1/categories/{_headCategoryId}/settings",
            new UpdateCategorySettingsRequest(
                "Asia/Dushanbe", dueDays, new TimeOnly(23, 59), reminderHours, true, settings.ConcurrencyToken));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ValidPassword));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Authorization =
            new("Bearer", (await ReadAsync<LoginResponse>(response)).AccessToken);

        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<T>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private async Task SeedAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var passwordHash = new Pbkdf2PasswordHasher().Hash(ValidPassword);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Seeded);
        var khujand = Branch.Create(organization.Id, "Филиал Худжанд", "KHJ", null, "Asia/Dushanbe", Seeded);
        context.Branches.AddRange(headOffice, khujand);

        // Two categories with the same name in different branches — the minimal configuration that
        // exposes a grouping-by-name defect (TEN-044a).
        var headCategory = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        var khujandCategory = Category.Create(organization.Id, khujand.Id, "C#", null, Seeded);
        context.Categories.AddRange(headCategory, khujandCategory);

        context.CategorySettings.AddRange(
            CategorySettings.CreateDefault(headCategory, headOffice.TimeZoneId, Seeded),
            CategorySettings.CreateDefault(khujandCategory, khujand.TimeZoneId, Seeded));

        var users = new List<User>
        {
            User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded),
            User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded),
            User.CreateLead(organization.Id, headOffice.Id, headCategory.Id, "Лид", "lead-head@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, khujand.Id, khujandCategory.Id, "Ментор Худжанда", "mentor-khujand@mentortaskflow.test", Seeded),
        };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        _headOfficeId = headOffice.Id;
        _khujandId = khujand.Id;
        _headCategoryId = headCategory.Id;
        _khujandCategoryId = khujandCategory.Id;
    }
}
