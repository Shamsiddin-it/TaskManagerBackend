using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Telegram;
using MentorTaskFlow.Domain.Auditing;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MentorTaskFlow.IntegrationTests.Telegram;

/// <summary>Account binding, the bind token and the webhook (TZ 19).</summary>
[Collection(PostgresCollection.Name)]
public sealed class TelegramBindingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string ChatId = "987654321";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private MentorTaskFlowApiFactory _disabled = null!;
    private Guid _mentorId;

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        await SeedAsync();

        _factory = new MentorTaskFlowApiFactory
        {
            ConnectionStringOverride = postgres.ConnectionString,
            TelegramEnabled = true,
        };

        _disabled = new MentorTaskFlowApiFactory { ConnectionStringOverride = postgres.ConnectionString };
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        _disabled.Dispose();

        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------
    // Issuing a token (TG-005, TG-006, TG-013)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_user_receives_a_token_and_a_deep_link()
    {
        using var mentor = await SignInAsync();

        var response = await mentor.PostAsync("/api/v1/telegram/bind-token", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        var token = await ReadAsync<TelegramBindTokenDto>(response);

        // TG-011: 32 random bytes, Base64Url, 43 characters with no padding.
        token.Token.Length.ShouldBe(43);
        token.DeepLink.ShouldBe($"https://t.me/mentortaskflow_test_bot?start={token.Token}");
        token.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    /// <summary><c>TG-012</c>: only the hash is stored, so a database read cannot yield the link.</summary>
    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored()
    {
        using var mentor = await SignInAsync();
        var token = await ReadAsync<TelegramBindTokenDto>(await mentor.PostAsync("/api/v1/telegram/bind-token", null));

        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var stored = await context.TelegramBindTokens.SingleAsync();

        stored.TokenHash.ShouldNotBe(token.Token);
        stored.TokenHash.Length.ShouldBe(64);
        stored.ExpiresAt.ShouldBe(stored.CreatedAt.AddMinutes(15), TimeSpan.FromSeconds(1));
    }

    /// <summary><c>TG-006</c>: a link that leaked stops working when its owner asks for another.</summary>
    [Fact]
    public async Task A_new_token_retires_the_previous_one()
    {
        using var mentor = await SignInAsync();

        var first = await ReadAsync<TelegramBindTokenDto>(await mentor.PostAsync("/api/v1/telegram/bind-token", null));
        await mentor.PostAsync("/api/v1/telegram/bind-token", null);

        (await PostWebhookAsync(_factory, $"/start {first.Token}"))
            .ShouldContain("недействительна");

        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        (await context.TelegramBindTokens.CountAsync(t => t.UsedAt == null)).ShouldBe(1);
    }

    /// <summary><c>TG-014</c>: five issues an hour per user.</summary>
    [Fact]
    public async Task Issuing_is_limited_to_five_an_hour()
    {
        using var mentor = await SignInAsync();

        for (var i = 0; i < 5; i++)
        {
            (await mentor.PostAsync("/api/v1/telegram/bind-token", null))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await mentor.PostAsync("/api/v1/telegram/bind-token", null))
            .StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    // -----------------------------------------------------------------
    // Redeeming (TG-007, TG-008, TG-009)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_valid_token_binds_the_account_that_issued_it()
    {
        using var mentor = await SignInAsync();
        var token = await ReadAsync<TelegramBindTokenDto>(await mentor.PostAsync("/api/v1/telegram/bind-token", null));

        (await PostWebhookAsync(_factory, $"/start {token.Token}")).ShouldContain("Готово");

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.Users.SingleAsync(u => u.Id == _mentorId)).TelegramChatId.ShouldBe(ChatId);
        (await context.TelegramBindTokens.SingleAsync()).UsedAt.ShouldNotBeNull();

        // TG-015: the outcome is recorded, and the record carries neither the token nor the chat.
        var audit = await context.AuditLogs.SingleAsync(a => a.Action == AuditActions.TelegramBind);
        audit.Result.ShouldBe(AuditResult.Success);
        audit.EntityId.ShouldBe(_mentorId);
        audit.Metadata!.RootElement.ToString().ShouldNotContain(token.Token);
        audit.Metadata.RootElement.ToString().ShouldNotContain(ChatId);
    }

    /// <summary>A token is single-use even inside its fifteen minutes (<c>TG-007</c>).</summary>
    [Fact]
    public async Task A_token_cannot_be_redeemed_twice()
    {
        using var mentor = await SignInAsync();
        var token = await ReadAsync<TelegramBindTokenDto>(await mentor.PostAsync("/api/v1/telegram/bind-token", null));

        await PostWebhookAsync(_factory, $"/start {token.Token}");

        (await PostWebhookAsync(_factory, $"/start {token.Token}", chatId: "111222333"))
            .ShouldContain("недействительна");
    }

    /// <summary>
    /// Every failure answers alike. Telling «no such token» apart from «expired» would confirm to
    /// whoever is guessing that a value was otherwise real.
    /// </summary>
    [Theory]
    [InlineData("/start totally-made-up-token-value-that-is-wrong")]
    [InlineData("/start")]
    public async Task An_unusable_token_is_refused_with_one_message(string command) =>
        (await PostWebhookAsync(_factory, command)).ShouldNotContain("Готово");

    /// <summary><c>TG-008</c>: two people sharing a device must not receive each other's notifications.</summary>
    [Fact]
    public async Task One_chat_cannot_serve_two_accounts()
    {
        using var mentor = await SignInAsync();
        var first = await ReadAsync<TelegramBindTokenDto>(await mentor.PostAsync("/api/v1/telegram/bind-token", null));
        await PostWebhookAsync(_factory, $"/start {first.Token}");

        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var second = await ReadAsync<TelegramBindTokenDto>(await lead.PostAsync("/api/v1/telegram/bind-token", null));

        (await PostWebhookAsync(_factory, $"/start {second.Token}")).ShouldContain("уже привязан");

        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        (await context.Users.CountAsync(u => u.TelegramChatId == ChatId)).ShouldBe(1);
    }

    /// <summary>
    /// <c>TG-009</c>: the account bound is the token's owner. Nothing in the Telegram payload chooses
    /// it, so another person's binding cannot be claimed without their token.
    /// </summary>
    [Fact]
    public async Task The_chat_does_not_choose_which_account_is_bound()
    {
        using var lead = await SignInAsync("lead-sharp@mentortaskflow.test");
        var token = await ReadAsync<TelegramBindTokenDto>(await lead.PostAsync("/api/v1/telegram/bind-token", null));

        // The payload names a different user id; it is ignored entirely.
        await PostWebhookAsync(_factory, $"/start {token.Token}", extraUserId: _mentorId);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.Users.SingleAsync(u => u.Id == _mentorId)).TelegramChatId.ShouldBeNull();
        (await context.Users.SingleAsync(u => u.Email == "lead-sharp@mentortaskflow.test"))
            .TelegramChatId.ShouldBe(ChatId);
    }

    /// <summary><c>TG-004</c>: the bot performs no business actions.</summary>
    [Theory]
    [InlineData("/approve")]
    [InlineData("одобри задачу")]
    public async Task An_unrecognised_command_gets_help(string text) =>
        (await PostWebhookAsync(_factory, text)).ShouldContain("Действия по задачам выполняются в приложении");

    // -----------------------------------------------------------------
    // Webhook security (TG-002, TG-003)
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_webhook_without_the_secret_is_refused_without_a_body()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/telegram/webhook", Update("/start x"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_webhook_with_the_wrong_secret_is_refused()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", "not-the-secret");

        (await client.PostAsJsonAsync("/api/v1/telegram/webhook", Update("/start x")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>An update the bot does not handle is acknowledged so Telegram stops resending it.</summary>
    [Fact]
    public async Task An_unhandled_update_is_acknowledged()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", MentorTaskFlowApiFactory.TelegramWebhookSecret);

        var response = await client.PostAsJsonAsync(
            "/api/v1/telegram/webhook",
            JsonSerializer.Deserialize<JsonElement>("""{"update_id":1,"edited_message":{}}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------
    // Status, unbinding and the feature flag
    // -----------------------------------------------------------------

    [Fact]
    public async Task Status_reports_the_binding()
    {
        using var mentor = await SignInAsync();

        (await ReadAsync<TelegramStatusDto>(await mentor.GetAsync("/api/v1/telegram/status")))
            .IsBound.ShouldBeFalse();

        var token = await ReadAsync<TelegramBindTokenDto>(await mentor.PostAsync("/api/v1/telegram/bind-token", null));
        await PostWebhookAsync(_factory, $"/start {token.Token}");

        var bound = await ReadAsync<TelegramStatusDto>(await mentor.GetAsync("/api/v1/telegram/status"));
        bound.IsBound.ShouldBeTrue();
        bound.BoundAt.ShouldNotBeNull();
    }

    /// <summary>
    /// <c>TG-010</c>: unbinding silences nothing — <c>TelegramPreferred</c> events go by email after
    /// it (<c>NTF-002</c>).
    /// </summary>
    [Fact]
    public async Task Unbinding_clears_the_chat_and_is_audited()
    {
        using var mentor = await SignInAsync();
        var token = await ReadAsync<TelegramBindTokenDto>(await mentor.PostAsync("/api/v1/telegram/bind-token", null));
        await PostWebhookAsync(_factory, $"/start {token.Token}");

        (await mentor.DeleteAsync("/api/v1/telegram/binding")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var context = postgres.CreateContext(suppressTenantFilter: true);

        (await context.Users.SingleAsync(u => u.Id == _mentorId)).TelegramChatId.ShouldBeNull();
        (await context.AuditLogs.AnyAsync(a => a.Action == AuditActions.TelegramUnbind)).ShouldBeTrue();
    }

    [Fact]
    public async Task Unbinding_without_a_binding_is_not_an_error()
    {
        using var mentor = await SignInAsync();

        (await mentor.DeleteAsync("/api/v1/telegram/binding")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// 4.1: with the feature off the endpoints answer 404. A capability the installation does not have
    /// should be indistinguishable from one that does not exist.
    /// </summary>
    [Fact]
    public async Task The_endpoints_disappear_when_the_feature_is_off()
    {
        using var mentor = await SignInAsync(factory: _disabled);

        (await mentor.PostAsync("/api/v1/telegram/bind-token", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await mentor.GetAsync("/api/v1/telegram/status")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await mentor.DeleteAsync("/api/v1/telegram/binding")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_webhook_is_closed_when_the_feature_is_off()
    {
        using var client = _disabled.CreateClient();
        client.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", MentorTaskFlowApiFactory.TelegramWebhookSecret);

        (await client.PostAsJsonAsync("/api/v1/telegram/webhook", Update("/start x")))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_issue_a_token()
    {
        using var client = _factory.CreateClient();

        (await client.PostAsync("/api/v1/telegram/bind-token", null))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    private static async Task<string> PostWebhookAsync(
        MentorTaskFlowApiFactory factory,
        string text,
        string chatId = ChatId,
        Guid? extraUserId = null)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", MentorTaskFlowApiFactory.TelegramWebhookSecret);

        var response = await client.PostAsJsonAsync("/api/v1/telegram/webhook", Update(text, chatId, extraUserId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadAsStringAsync();
    }

    private static JsonElement Update(string text, string chatId = ChatId, Guid? extraUserId = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            update_id = 1,
            message = new
            {
                message_id = 1,
                chat = new { id = long.Parse(chatId), type = "private" },
                from = new { id = long.Parse(chatId), mentortaskflow_user_id = extraUserId },
                text,
            },
        });

        return JsonSerializer.Deserialize<JsonElement>(payload);
    }

    private async Task<HttpClient> SignInAsync(
        string email = "mentor-head@mentortaskflow.test",
        MentorTaskFlowApiFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();

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

    private async Task SeedAsync()
    {
        await using var context = postgres.CreateContext(suppressTenantFilter: true);
        var passwordHash = new Pbkdf2PasswordHasher().Hash(ValidPassword);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, "Asia/Dushanbe", Seeded);
        context.Branches.Add(headOffice);

        var sharp = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        context.Categories.Add(sharp);
        context.CategorySettings.Add(CategorySettings.CreateDefault(sharp, headOffice.TimeZoneId, Seeded));

        var lead = User.CreateLead(organization.Id, headOffice.Id, sharp.Id, "Лид C#", "lead-sharp@mentortaskflow.test", Seeded);
        var mentor = User.CreateMentor(organization.Id, headOffice.Id, sharp.Id, "Ментор", "mentor-head@mentortaskflow.test", Seeded);

        foreach (var user in new[] { lead, mentor })
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(lead, mentor);
        await context.SaveChangesAsync();

        _mentorId = mentor.Id;
    }
}
