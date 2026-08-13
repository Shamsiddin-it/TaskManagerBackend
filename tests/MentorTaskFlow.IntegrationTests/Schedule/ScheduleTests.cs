using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Contracts.Schedule;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;

namespace MentorTaskFlow.IntegrationTests.Schedule;

/// <summary>The category schedule (Приложение D.4, TZ 15.4).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ScheduleTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string BranchHeader = "X-MTF-Branch-Id";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

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
    // Mentor is read-only (TOPIC-004)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_mentor_reads_the_schedule_of_their_own_category()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        await CreateTopicAsync(lead, dayNumber: 1, title: "Введение в C#");

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");
        var page = await ReadAsync<PagedResult<TopicDto>>(await mentor.GetAsync("/api/v1/topics"));

        page.Items.ShouldHaveSingleItem().CategoryId.ShouldBe(_headCategoryId);
    }

    /// <summary>
    /// The plan is the Lead's instrument. A mentor able to edit it could hand themselves work other
    /// than the curriculum prescribes (<c>TOPIC-004</c>).
    /// </summary>
    [Fact]
    public async Task A_mentor_cannot_change_the_schedule()
    {
        using var lead = await SignInAsync("lead-head@mentortaskflow.test");
        var topic = await CreateTopicAsync(lead, dayNumber: 1, title: "Введение в C#");

        using var mentor = await SignInAsync("mentor-head@mentortaskflow.test");

        var create = await mentor.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(_headCategoryId, 2, null, "Своя тема", null));
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var update = await mentor.PutAsJsonAsync($"/api/v1/topics/{topic.Id}",
            new UpdateTopicRequest(1, null, "Переписано", null, topic.ConcurrencyToken));
        update.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await mentor.DeleteAsync($"/api/v1/topics/{topic.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Uniqueness (TOPIC-001, TOPIC-002, TOPIC-010)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_duplicate_day_number_in_one_category_is_a_conflict()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");
        await CreateTopicAsync(client, dayNumber: 1, title: "Введение в C#");

        var response = await client.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(_headCategoryId, 1, null, "Другая тема", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ResourceAlreadyExists);
    }

    [Fact]
    public async Task A_duplicate_planned_date_among_active_topics_is_a_conflict()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");
        var date = new DateOnly(2026, 9, 1);

        await CreateTopicAsync(client, dayNumber: 1, title: "Введение в C#", plannedDate: date);

        var response = await client.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(_headCategoryId, 2, date, "Вторая тема", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ResourceAlreadyExists);
    }

    /// <summary>
    /// The index is partial. Two archived topics may share a date: the constraint exists to keep the
    /// scheduler's selection unambiguous, not to police history (<c>TOPIC-010</c>).
    /// </summary>
    [Fact]
    public async Task An_archived_topic_frees_its_planned_date()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");
        var date = new DateOnly(2026, 9, 1);

        var first = await CreateTopicAsync(client, dayNumber: 1, title: "Введение в C#", plannedDate: date);

        await client.PostAsJsonAsync($"/api/v1/topics/{first.Id}/deactivate",
            new ScheduleActionRequest(first.ConcurrencyToken));

        var second = await client.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(_headCategoryId, 2, date, "Замена", null));

        second.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// <c>TOPIC-011</c>: schedules are routinely backfilled when a course is rescheduled, so a past
    /// date is accepted and the interface warns instead.
    /// </summary>
    [Fact]
    public async Task A_planned_date_in_the_past_is_accepted()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(_headCategoryId, 1, new DateOnly(2020, 1, 1), "Задним числом", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_same_day_number_is_free_in_another_category()
    {
        using var headLead = await SignInAsync("lead-head@mentortaskflow.test");
        await CreateTopicAsync(headLead, dayNumber: 1, title: "Введение в C#");

        using var khujandLead = await SignInAsync("lead-khujand@mentortaskflow.test");

        var response = await khujandLead.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(_khujandCategoryId, 1, null, "Введение в C#", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    // -----------------------------------------------------------------
    // Scope (TEN-006, TPL-005)
    // -----------------------------------------------------------------

    /// <summary>A Lead works in exactly one category and cannot plan another's curriculum.</summary>
    [Fact]
    public async Task A_lead_cannot_create_a_topic_in_another_category()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(_khujandCategoryId, 1, null, "Чужая тема", null));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_topic_of_another_branch_answers_not_found()
    {
        using var khujandLead = await SignInAsync("lead-khujand@mentortaskflow.test");
        var foreign = await CreateTopicAsync(khujandLead, dayNumber: 1, title: "Тема Худжанда", categoryId: _khujandCategoryId);

        using var headLead = await SignInAsync("lead-head@mentortaskflow.test");
        var response = await headLead.GetAsync($"/api/v1/topics/{foreign.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task An_organization_admin_must_choose_a_branch_to_create()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");

        var response = await client.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(_headCategoryId, 1, null, "Тема", null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.BranchContextRequired);
    }

    [Fact]
    public async Task An_organization_admin_creates_in_the_selected_branch()
    {
        using var client = await SignInAsync("organization-admin@mentortaskflow.test");
        client.DefaultRequestHeaders.Add(BranchHeader, _khujandId.ToString());

        var response = await client.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(_khujandCategoryId, 1, null, "Тема", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReadAsync<TopicDto>(response)).BranchId.ShouldBe(_khujandId);
    }

    // -----------------------------------------------------------------
    // Templates (TPL-001, TPL-002)
    // -----------------------------------------------------------------

    /// <summary>
    /// The template inherits its scope from the topic in the route, which is what makes <c>TPL-001</c>
    /// enforceable rather than merely stated.
    /// </summary>
    [Fact]
    public async Task A_template_inherits_the_scope_of_its_topic()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");
        var topic = await CreateTopicAsync(client, dayNumber: 1, title: "Введение в C#");

        var response = await client.PostAsJsonAsync($"/api/v1/topics/{topic.Id}/assignments",
            new CreateTopicAssignmentRequest("HomeTask", "Домашнее задание", "Сделать презентацию"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var template = await ReadAsync<TopicAssignmentDto>(response);
        template.TopicId.ShouldBe(topic.Id);
        template.BranchId.ShouldBe(_headOfficeId);
        template.CategoryId.ShouldBe(_headCategoryId);
        template.IsRequired.ShouldBeTrue();
        template.IsActive.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Presentation")]
    [InlineData("ClassTask")]
    [InlineData("HomeTask")]
    public async Task Every_documented_template_type_is_accepted(string type)
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");
        var topic = await CreateTopicAsync(client, dayNumber: 1, title: "Введение в C#");

        var response = await client.PostAsJsonAsync($"/api/v1/topics/{topic.Id}/assignments",
            new CreateTopicAssignmentRequest(type, "Задание", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReadAsync<TopicAssignmentDto>(response)).Type.ShouldBe(type);
    }

    [Fact]
    public async Task An_unknown_template_type_is_refused()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");
        var topic = await CreateTopicAsync(client, dayNumber: 1, title: "Введение в C#");

        var response = await client.PostAsJsonAsync($"/api/v1/topics/{topic.Id}/assignments",
            new CreateTopicAssignmentRequest("Homework", "Задание", null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    // -----------------------------------------------------------------
    // Deletion (TOPIC-003, TOPIC-012)
    // -----------------------------------------------------------------

    /// <summary>
    /// Deleting a topic that still carries templates would orphan work already planned, so it is
    /// refused and deactivation is offered instead (<c>TOPIC-003</c>).
    /// </summary>
    [Fact]
    public async Task A_topic_with_templates_cannot_be_deleted()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");
        var topic = await CreateTopicAsync(client, dayNumber: 1, title: "Введение в C#");

        await client.PostAsJsonAsync($"/api/v1/topics/{topic.Id}/assignments",
            new CreateTopicAssignmentRequest("HomeTask", "Домашнее задание", null));

        var response = await client.DeleteAsync($"/api/v1/topics/{topic.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ResourceInUse);

        // The documented alternative works.
        var current = await ReadAsync<TopicDto>(await client.GetAsync($"/api/v1/topics/{topic.Id}"));
        var deactivated = await client.PostAsJsonAsync($"/api/v1/topics/{topic.Id}/deactivate",
            new ScheduleActionRequest(current.ConcurrencyToken));

        deactivated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync<TopicDto>(deactivated)).IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unreferenced_topic_is_deleted()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");
        var topic = await CreateTopicAsync(client, dayNumber: 1, title: "Введение в C#");

        (await client.DeleteAsync($"/api/v1/topics/{topic.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/v1/topics/{topic.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_template_is_deleted_while_nothing_references_it()
    {
        using var client = await SignInAsync("lead-head@mentortaskflow.test");
        var topic = await CreateTopicAsync(client, dayNumber: 1, title: "Введение в C#");

        var template = await ReadAsync<TopicAssignmentDto>(await client.PostAsJsonAsync(
            $"/api/v1/topics/{topic.Id}/assignments",
            new CreateTopicAssignmentRequest("HomeTask", "Домашнее задание", null)));

        (await client.DeleteAsync($"/api/v1/topic-assignments/{template.Id}"))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // -----------------------------------------------------------------
    // Deactivated contours (CAT-010, BRN-032)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_deactivated_category_refuses_schedule_changes()
    {
        using var admin = await SignInAsync("branch-admin-head@mentortaskflow.test");

        var category = await ReadAsync<Contracts.Categories.CategoryDto>(
            await admin.GetAsync($"/api/v1/categories/{_headCategoryId}"));

        await admin.PostAsJsonAsync($"/api/v1/categories/{_headCategoryId}/deactivate",
            new Contracts.Categories.DeactivateCategoryRequest(category.ConcurrencyToken, ConfirmActiveUsers: true));

        using var lead = await SignInAsync("lead-head@mentortaskflow.test");

        var response = await lead.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(_headCategoryId, 1, null, "Тема", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.CategoryInactive);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<TopicDto> CreateTopicAsync(
        HttpClient client,
        int dayNumber,
        string title,
        DateOnly? plannedDate = null,
        Guid? categoryId = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/topics",
            new CreateTopicRequest(categoryId ?? _headCategoryId, dayNumber, plannedDate, title, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return await ReadAsync<TopicDto>(response);
    }

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
            User.CreateLead(organization.Id, khujand.Id, khujandCategory.Id, "Лид Худжанда", "lead-khujand@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded),
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
