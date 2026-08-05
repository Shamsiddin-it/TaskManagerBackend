using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MentorTaskFlow.Api.Authentication;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Identity;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.IntegrationTests.Persistence;
using MentorTaskFlow.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MentorTaskFlow.IntegrationTests.Identity;

/// <summary>
/// The authentication flows of TZ 16 against a real database and the real API pipeline.
/// </summary>
/// <remarks>
/// The fixture mirrors the mock users of TZ 41.3: one organization, a head office and the Khujand
/// branch, a `C#` category in each, and one account per user type.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AuthFlowTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _organizationId;
    private Guid _headOfficeId;
    private Guid _categoryId;

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
    // Login
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_correct_password_returns_the_profile_and_sets_both_cookies()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("organization-admin@mentortaskflow.test", ValidPassword));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await ReadAsync<LoginResponse>(response);
        body.User.Email.ShouldBe("organization-admin@mentortaskflow.test");
        body.AccessToken.ShouldNotBeNullOrWhiteSpace();

        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        cookies.ShouldContain(c => c.StartsWith(AuthCookieManager.RefreshTokenCookieName));
        cookies.ShouldContain(c => c.StartsWith(AuthCookieManager.CsrfCookieName));

        // The refresh token lives only in an HttpOnly cookie; page script must not be able to read it
        // (AUTH-010).
        cookies.Single(c => c.StartsWith(AuthCookieManager.RefreshTokenCookieName))
            .ShouldContain("httponly", Case.Insensitive);

        // The CSRF half of the double submit is deliberately readable — the client has to echo it.
        cookies.Single(c => c.StartsWith(AuthCookieManager.CsrfCookieName))
            .ShouldNotContain("httponly", Case.Insensitive);

        // API-019: no shared cache or back button may resurface an access token.
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task The_response_body_never_carries_the_refresh_token()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("organization-admin@mentortaskflow.test", ValidPassword));

        var refreshToken = ExtractCookie(response, AuthCookieManager.RefreshTokenCookieName);
        var raw = await response.Content.ReadAsStringAsync();

        raw.ShouldNotContain(refreshToken);
    }

    [Fact]
    public async Task A_wrong_password_is_rejected()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("organization-admin@mentortaskflow.test", "WrongPassword2026"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.InvalidCredentials);
    }

    /// <summary>
    /// An unknown address and a wrong password answer identically. Anything else turns login into an
    /// account-enumeration oracle (<c>AUTH-024</c>).
    /// </summary>
    [Fact]
    public async Task An_unknown_address_is_indistinguishable_from_a_wrong_password()
    {
        using var client = CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("nobody@mentortaskflow.test", ValidPassword));
        var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("organization-admin@mentortaskflow.test", "WrongPassword2026"));

        unknown.StatusCode.ShouldBe(wrongPassword.StatusCode);
        Normalize(await unknown.Content.ReadAsStringAsync())
            .ShouldBe(Normalize(await wrongPassword.Content.ReadAsStringAsync()));
    }

    /// <summary><c>USER-021</c>: an invited account with no password yet cannot sign in.</summary>
    [Fact]
    public async Task An_account_without_a_password_cannot_sign_in()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("invited@mentortaskflow.test", ValidPassword));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.InvalidCredentials);
    }

    [Fact]
    public async Task A_deactivated_account_is_told_so()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("deactivated@mentortaskflow.test", ValidPassword));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.UserDeactivated);
    }

    /// <summary>
    /// <c>AUTH-024</c>: five failures lock the account for 15 minutes, and the lockout answers with
    /// the same code as a wrong password so it cannot be probed.
    /// </summary>
    [Fact]
    public async Task Five_failures_lock_the_account_without_saying_so()
    {
        using var client = CreateClient();
        const string email = "lockout-target@mentortaskflow.test";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "WrongPassword2026"));
        }

        var afterLockout = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, ValidPassword));

        afterLockout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadCodeAsync(afterLockout)).ShouldBe(ErrorCodes.InvalidCredentials);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var user = await context.Users.FirstAsync(u => u.NormalizedEmail == email.ToUpperInvariant());
        user.LockoutUntil.ShouldNotBeNull();
        user.FailedLoginCount.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task A_successful_login_clears_the_failure_counter()
    {
        using var client = CreateClient();
        const string email = "counter-reset@mentortaskflow.test";

        await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "WrongPassword2026"));
        await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ValidPassword));

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var user = await context.Users.FirstAsync(u => u.NormalizedEmail == email.ToUpperInvariant());
        user.FailedLoginCount.ShouldBe(0);
        user.LastLoginAt.ShouldNotBeNull();
    }

    // -----------------------------------------------------------------
    // Profile contract (AUTH-037, AUTH-038)
    // -----------------------------------------------------------------

    public static TheoryData<string, string?, bool, bool> ProfileShapes() => new()
    {
        // email prefix, adminScope, expects branch, expects category
        { "organization-admin", "Organization", false, false },
        { "branch-admin-head", "Branch", true, false },
        { "lead-head", null, true, true },
        { "mentor-head", null, true, true },
    };

    [Theory]
    [MemberData(nameof(ProfileShapes))]
    public async Task The_profile_shape_matches_the_user_type(
        string emailPrefix,
        string? expectedAdminScope,
        bool expectsBranch,
        bool expectsCategory)
    {
        using var client = CreateClient();
        var login = await SignInAsync(client, $"{emailPrefix}@mentortaskflow.test");

        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        var response = await client.GetAsync("/api/v1/auth/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var profile = await ReadAsync<AuthUserDto>(response);

        profile.AdminScope.ShouldBe(expectedAdminScope);
        (profile.Branch is not null).ShouldBe(expectsBranch);
        (profile.CategoryId is not null).ShouldBe(expectsCategory);

        // Present for every user type — the minimal organization view of ORG-003.
        profile.Organization.Id.ShouldBe(_organizationId);
        profile.Organization.Name.ShouldBe("SoftClub Academy");
    }

    /// <summary>Login and <c>/auth/me</c> return the very same shape (<c>AUTH-037</c>).</summary>
    [Fact]
    public async Task Login_and_me_agree()
    {
        using var client = CreateClient();
        var login = await SignInAsync(client, "lead-head@mentortaskflow.test");

        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        var me = await ReadAsync<AuthUserDto>(await client.GetAsync("/api/v1/auth/me"));

        JsonSerializer.Serialize(me).ShouldBe(JsonSerializer.Serialize(login.User));
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_read_the_profile()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/v1/auth/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.Unauthorized);
    }

    // -----------------------------------------------------------------
    // Refresh rotation and reuse detection
    // -----------------------------------------------------------------

    [Fact]
    public async Task Refreshing_rotates_the_token()
    {
        using var client = CreateClient();
        var (refreshToken, csrf) = await SignInForCookiesAsync(client, "lead-head@mentortaskflow.test");

        var response = await PostWithCookiesAsync(client, "/api/v1/auth/refresh", refreshToken, csrf);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = ExtractCookie(response, AuthCookieManager.RefreshTokenCookieName);
        rotated.ShouldNotBe(refreshToken);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var stored = await context.RefreshTokens.OrderBy(t => t.CreatedAt).ToListAsync();
        stored.Count.ShouldBe(2);
        stored[0].ReasonRevoked.ShouldBe(RefreshTokenRevocationReason.Rotated);
        stored[0].ReplacedByTokenId.ShouldBe(stored[1].Id);

        // Rotation stays inside one family, which is what makes reuse detection able to revoke the
        // whole chain at once.
        stored[1].FamilyId.ShouldBe(stored[0].FamilyId);
    }

    /// <summary>
    /// <c>AUTH-008</c>: replaying a rotated token means the value leaked. The whole family is revoked
    /// and <c>TokenVersion</c> is incremented, so outstanding access tokens die too.
    /// </summary>
    [Fact]
    public async Task Replaying_a_rotated_token_revokes_the_entire_family()
    {
        using var client = CreateClient();
        var (original, csrf) = await SignInForCookiesAsync(client, "lead-head@mentortaskflow.test");

        var rotatedResponse = await PostWithCookiesAsync(client, "/api/v1/auth/refresh", original, csrf);
        var rotatedCsrf = ExtractCookie(rotatedResponse, AuthCookieManager.CsrfCookieName);

        var replay = await PostWithCookiesAsync(client, "/api/v1/auth/refresh", original, csrf);

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadCodeAsync(replay)).ShouldBe(ErrorCodes.RefreshTokenReuseDetected);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var stored = await context.RefreshTokens.ToListAsync();
        stored.ShouldAllBe(t => t.RevokedAt != null);
        stored.ShouldContain(t => t.ReasonRevoked == RefreshTokenRevocationReason.ReuseDetected);

        var user = await context.Users.FirstAsync(u => u.NormalizedEmail == "LEAD-HEAD@MENTORTASKFLOW.TEST");
        user.TokenVersion.ShouldBeGreaterThan(0);

        // The token issued moments earlier is dead too: the leak is assumed to cover the whole chain.
        var afterReuse = await PostWithCookiesAsync(
            client,
            "/api/v1/auth/refresh",
            ExtractCookie(rotatedResponse, AuthCookieManager.RefreshTokenCookieName),
            rotatedCsrf);

        afterReuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// <c>AUTH-034</c>: after the version bump, an access token minted before it stops being accepted.
    /// </summary>
    [Fact]
    public async Task An_access_token_dies_once_the_token_version_moves()
    {
        using var client = CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("mentor-head@mentortaskflow.test", ValidPassword));
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var login = await ReadAsync<LoginResponse>(loginResponse);
        var refreshToken = ExtractCookie(loginResponse, AuthCookieManager.RefreshTokenCookieName);
        var csrf = ExtractCookie(loginResponse, AuthCookieManager.CsrfCookieName);

        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        (await client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Force a bump through reuse detection, then reuse the same access token.
        await PostWithCookiesAsync(client, "/api/v1/auth/refresh", refreshToken, csrf);
        await PostWithCookiesAsync(client, "/api/v1/auth/refresh", refreshToken, csrf);

        var response = await client.GetAsync("/api/v1/auth/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.TokenVersionMismatch);
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_rejected()
    {
        using var client = CreateClient();
        var (_, csrf) = await SignInForCookiesAsync(client, "lead-head@mentortaskflow.test");

        var response = await PostWithCookiesAsync(client, "/api/v1/auth/refresh", "not-a-real-token", csrf);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.RefreshTokenInvalid);
    }

    // -----------------------------------------------------------------
    // CSRF (AUTH-012)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Refresh_without_the_csrf_header_is_refused()
    {
        using var client = CreateClient();
        var (refreshToken, csrf) = await SignInForCookiesAsync(client, "lead-head@mentortaskflow.test");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie",
            $"{AuthCookieManager.RefreshTokenCookieName}={refreshToken}; {AuthCookieManager.CsrfCookieName}={csrf}");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.CsrfValidationFailed);
    }

    [Fact]
    public async Task Refresh_with_a_mismatched_csrf_header_is_refused()
    {
        using var client = CreateClient();
        var (refreshToken, csrf) = await SignInForCookiesAsync(client, "lead-head@mentortaskflow.test");

        var response = await PostWithCookiesAsync(client, "/api/v1/auth/refresh", refreshToken, csrf, csrfHeader: "different");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.CsrfValidationFailed);
    }

    /// <summary>An origin outside the allowlist is refused even with a matching CSRF pair.</summary>
    [Fact]
    public async Task Refresh_from_a_foreign_origin_is_refused()
    {
        using var client = CreateClient();
        var (refreshToken, csrf) = await SignInForCookiesAsync(client, "lead-head@mentortaskflow.test");

        using var request = BuildCookieRequest(
            "/api/v1/auth/refresh", refreshToken, csrf, csrf, origin: "https://evil.example");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Logout
    // -----------------------------------------------------------------

    [Fact]
    public async Task Logout_revokes_the_token_and_clears_the_cookies()
    {
        using var client = CreateClient();
        var (refreshToken, csrf) = await SignInForCookiesAsync(client, "lead-head@mentortaskflow.test");

        var response = await PostWithCookiesAsync(client, "/api/v1/auth/logout", refreshToken, csrf);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var stored = await context.RefreshTokens.SingleAsync();
        stored.ReasonRevoked.ShouldBe(RefreshTokenRevocationReason.Logout);

        var refreshAfterLogout = await PostWithCookiesAsync(client, "/api/v1/auth/refresh", refreshToken, csrf);
        refreshAfterLogout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------
    // Password lifecycle
    // -----------------------------------------------------------------

    /// <summary><c>AUTH-015</c>: 202 with an identical body whether or not the address exists.</summary>
    [Fact]
    public async Task Forgot_password_answers_identically_for_known_and_unknown_addresses()
    {
        using var client = CreateClient();

        var known = await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest("lead-head@mentortaskflow.test"));
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest("nobody@mentortaskflow.test"));

        known.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await known.Content.ReadAsStringAsync()).ShouldBe(await unknown.Content.ReadAsStringAsync());
    }

    /// <summary>The link is never returned to the caller — otherwise anyone could reset any account.</summary>
    [Fact]
    public async Task Forgot_password_never_returns_the_token()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest("lead-head@mentortaskflow.test"));

        var body = await response.Content.ReadAsStringAsync();

        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var issued = await context.UserSecurityTokens
            .Where(t => t.Purpose == SecurityTokenPurpose.ResetPassword)
            .ToListAsync();

        issued.ShouldHaveSingleItem();
        body.ShouldNotContain(issued[0].TokenHash);
    }

    [Fact]
    public async Task An_invited_user_sets_a_password_and_can_then_sign_in()
    {
        var token = await IssueSetPasswordTokenAsync("invited@mentortaskflow.test");

        using var client = CreateClient();

        var setResponse = await client.PostAsJsonAsync("/api/v1/auth/set-password",
            new SetPasswordRequest(token, "Invited2026Task"));
        setResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest("invited@mentortaskflow.test", "Invited2026Task"));
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Single use: a redeemed token cannot be replayed (<c>AUTH-030</c>).</summary>
    [Fact]
    public async Task A_set_password_token_works_only_once()
    {
        var token = await IssueSetPasswordTokenAsync("invited@mentortaskflow.test");

        using var client = CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/set-password", new SetPasswordRequest(token, "Invited2026Task"));
        var replay = await client.PostAsJsonAsync("/api/v1/auth/set-password",
            new SetPasswordRequest(token, "Another2026Task"));

        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(replay)).ShouldBe(ErrorCodes.SecurityTokenInvalid);
    }

    [Fact]
    public async Task An_unknown_token_is_refused_with_the_same_code_as_a_spent_one()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/set-password",
            new SetPasswordRequest("made-up-token", "Invited2026Task"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.SecurityTokenInvalid);
    }

    /// <summary>
    /// The password policy is checked only after the token proves usable, so a holder of an invalid
    /// token learns nothing about the rules — or about whether their guess was otherwise valid.
    /// </summary>
    [Fact]
    public async Task A_weak_password_is_refused_only_once_the_token_is_valid()
    {
        var token = await IssueSetPasswordTokenAsync("invited@mentortaskflow.test");

        using var client = CreateClient();

        var weakWithBadToken = await client.PostAsJsonAsync("/api/v1/auth/set-password",
            new SetPasswordRequest("made-up-token", "short"));
        (await ReadCodeAsync(weakWithBadToken)).ShouldBe(ErrorCodes.SecurityTokenInvalid);

        var weakWithGoodToken = await client.PostAsJsonAsync("/api/v1/auth/set-password",
            new SetPasswordRequest(token, "short"));
        (await ReadCodeAsync(weakWithGoodToken)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Changing_a_password_revokes_other_sessions_but_keeps_the_caller_signed_in()
    {
        using var client = CreateClient();
        var first = await SignInAsync(client, "mentor-head@mentortaskflow.test");

        // A second session, as if from another device.
        using var otherDevice = CreateClient();
        var (otherRefreshToken, otherCsrf) = await SignInForCookiesAsync(otherDevice, "mentor-head@mentortaskflow.test");

        client.DefaultRequestHeaders.Authorization = new("Bearer", first.AccessToken);
        var change = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest(ValidPassword, "Rotated2026Task"));

        change.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The caller receives a working pair in the very same response (AUTH-014).
        var refreshed = await ReadAsync<LoginResponse>(change);
        refreshed.AccessToken.ShouldNotBeNullOrWhiteSpace();

        var otherAfterChange = await PostWithCookiesAsync(
            otherDevice, "/api/v1/auth/refresh", otherRefreshToken, otherCsrf);
        otherAfterChange.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Changing_a_password_requires_the_current_one()
    {
        using var client = CreateClient();
        var login = await SignInAsync(client, "mentor-head@mentortaskflow.test");

        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest("WrongCurrent2026", "Rotated2026Task"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private HttpClient CreateClient() =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

    private static async Task<LoginResponse> SignInAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ValidPassword));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await ReadAsync<LoginResponse>(response);
    }

    private static async Task<(string RefreshToken, string Csrf)> SignInForCookiesAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ValidPassword));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (ExtractCookie(response, AuthCookieManager.RefreshTokenCookieName),
                ExtractCookie(response, AuthCookieManager.CsrfCookieName));
    }

    private static Task<HttpResponseMessage> PostWithCookiesAsync(
        HttpClient client,
        string path,
        string refreshToken,
        string csrf,
        string? csrfHeader = null)
    {
        var request = BuildCookieRequest(path, refreshToken, csrf, csrfHeader ?? csrf);
        return client.SendAsync(request);
    }

    private static HttpRequestMessage BuildCookieRequest(
        string path,
        string refreshToken,
        string csrfCookie,
        string csrfHeader,
        string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie",
            $"{AuthCookieManager.RefreshTokenCookieName}={refreshToken}; {AuthCookieManager.CsrfCookieName}={csrfCookie}");
        request.Headers.Add(AuthCookieManager.CsrfHeaderName, csrfHeader);
        request.Headers.Add("Origin", origin ?? MentorTaskFlowApiFactory.AllowedOrigin);
        return request;
    }

    private static string ExtractCookie(HttpResponseMessage response, string name)
    {
        var header = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith($"{name}="));
        var value = header[(name.Length + 1)..];
        var end = value.IndexOf(';');
        return end < 0 ? value : value[..end];
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

    private static string Normalize(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "\"traceId\":\"*\"");

    /// <summary>
    /// Plants an invitation token and returns its plain value.
    /// </summary>
    /// <remarks>
    /// Written through the same <see cref="SecureTokenService"/> the production path uses, rather than
    /// by reaching into internals: the test then proves that a token minted the ordinary way is
    /// accepted by the endpoint, which is the behaviour that matters.
    /// </remarks>
    private async Task<string> IssueSetPasswordTokenAsync(string email)
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);

        var user = await context.Users
            .IgnoreQueryFilters()
            .FirstAsync(u => u.NormalizedEmail == email.ToUpperInvariant());

        var (plainToken, tokenHash) = new SecureTokenService().Generate();

        context.UserSecurityTokens.Add(UserSecurityToken.Issue(
            user.Id,
            SecurityTokenPurpose.SetPassword,
            tokenHash,
            TimeSpan.FromHours(24),
            createdByIp: null,
            DateTimeOffset.UtcNow));

        await context.SaveChangesAsync();

        return plainToken;
    }

    private async Task SeedAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var hasher = new Pbkdf2PasswordHasher();
        var passwordHash = hasher.Hash(ValidPassword);

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

        // The seven mock users of TZ 41.3, plus three accounts for the negative paths.
        var users = new List<User>
        {
            User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded),
            User.CreateBranchAdmin(organization.Id, headOffice.Id, "Дилшод Рахимов", "branch-admin-head@mentortaskflow.test", Seeded),
            User.CreateBranchAdmin(organization.Id, khujand.Id, "Фируз Назаров", "branch-admin-khujand@mentortaskflow.test", Seeded),
            User.CreateLead(organization.Id, headOffice.Id, headCategory.Id, "Лид Главного офиса", "lead-head@mentortaskflow.test", Seeded),
            User.CreateLead(organization.Id, khujand.Id, khujandCategory.Id, "Лид Худжанда", "lead-khujand@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Ментор Главного офиса", "mentor-head@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, khujand.Id, khujandCategory.Id, "Ментор Худжанда", "mentor-khujand@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Заблокированный", "lockout-target@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Счётчик", "counter-reset@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Деактивированный", "deactivated@mentortaskflow.test", Seeded),
        };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        // Invited: no password yet, so login must fail while set-password must work (USER-021).
        var invited = User.CreateMentor(organization.Id, headOffice.Id, headCategory.Id, "Приглашённый", "invited@mentortaskflow.test", Seeded);
        users.Add(invited);

        users.Single(u => u.Email == "deactivated@mentortaskflow.test").Deactivate(Seeded);

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        _organizationId = organization.Id;
        _headOfficeId = headOffice.Id;
        _categoryId = headCategory.Id;
    }
}
