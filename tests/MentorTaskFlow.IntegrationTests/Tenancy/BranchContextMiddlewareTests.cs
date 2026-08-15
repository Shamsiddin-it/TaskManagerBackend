using System.Net;
using System.Security.Claims;
using System.Text.Json;
using MentorTaskFlow.Api.Middleware;
using MentorTaskFlow.Api.Options;
using MentorTaskFlow.Api.Tenancy;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MentorTaskFlow.IntegrationTests.Tenancy;

/// <summary>
/// The <c>X-MTF-Branch-Id</c> rules of TZ 38.3.
/// </summary>
/// <remarks>
/// Covers <c>TEST-TEN-007</c> (the header is refused for every role but Organization Admin),
/// <c>TEST-TEN-012</c> (a mutation without a chosen branch) and <c>TEST-TEN-013</c> (a branch of
/// another organization answers 404, never 403).
/// </remarks>
public sealed class BranchContextMiddlewareTests : IAsyncLifetime
{
    private static readonly Guid OrganizationA = Guid.CreateVersion7();
    private static readonly Guid OrganizationB = Guid.CreateVersion7();
    private static readonly Guid BranchOfA = Guid.CreateVersion7();
    private static readonly Guid BranchOfB = Guid.CreateVersion7();

    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddMetrics();
                    services.AddMemoryCache();

                    // The resolver turns tenant ids into the Slug/Code labels OBS-011 permits. This
                    // host has no database, so every lookup falls back to "unknown" — which is the
                    // documented behaviour and exactly what the middleware under test needs.
                    services.AddSingleton<TenantLabelResolver>();
                    services.AddSingleton<TenancyMetrics>();
                    services.AddOptions<TenancyOptions>();
                    services.AddHttpContextAccessor();
                    services.AddScoped<ICurrentUserAccessor, HttpCurrentUserAccessor>();
                    services.AddScoped<RequestBranchContext>();
                    services.AddScoped<IBranchContext>(sp => sp.GetRequiredService<RequestBranchContext>());
                    services.AddScoped<TenantFilterState>();

                    // Only branches of organization A exist, so a header naming BranchOfB is
                    // indistinguishable from a header naming a branch that was never created.
                    services.AddScoped<IBranchScopeValidator>(_ => new StubBranchScopeValidator());
                })
                .Configure(app =>
                {
                    app.UseCorrelationId();
                    app.UseMentorTaskFlowExceptionHandling();
                    app.UseMiddleware<TestAuthenticationMiddleware>();
                    app.UseBranchContext();

                    app.Run(async context =>
                    {
                        var branchContext = context.RequestServices.GetRequiredService<IBranchContext>();

                        // /mutate models a branch-scoped write; /read models an aggregate read.
                        var branchId = context.Request.Path.StartsWithSegments("/mutate")
                            ? branchContext.RequireBranchForMutation()
                            : branchContext.EffectiveBranchId;

                        await context.Response.WriteAsJsonAsync(new
                        {
                            organizationId = branchContext.EffectiveOrganizationId,
                            branchId,
                            allBranches = branchContext.IsAllBranchesReadContext,
                        });
                    });
                }))
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ---------------------------------------------------------------------
    // TEST-TEN-007 — the header is forbidden for Branch Admin, Lead and Mentor
    // ---------------------------------------------------------------------

    public static TheoryData<string, string?> RolesThatMayNotOverrideScope() => new()
    {
        { nameof(UserRole.Admin), nameof(AdminScope.Branch) },
        { nameof(UserRole.Lead), null },
        { nameof(UserRole.Mentor), null },
    };

    [Theory]
    [MemberData(nameof(RolesThatMayNotOverrideScope))]
    public async Task Header_from_a_non_organization_admin_is_refused(string role, string? adminScope)
    {
        var response = await SendAsync("/read", role, adminScope, branchHeader: BranchOfA.ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ScopeOverrideForbidden);
    }

    /// <summary>
    /// Refused even when the value is the caller's own branch. Any presence of the header from these
    /// roles is a client defect or a bypass attempt, and both warrant observation (<c>TEN-032</c>).
    /// </summary>
    [Fact]
    public async Task Header_naming_the_callers_own_branch_is_still_refused()
    {
        var response = await SendAsync("/read", nameof(UserRole.Lead), null, branchHeader: BranchOfA.ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ScopeOverrideForbidden);
    }

    [Fact]
    public async Task Without_the_header_a_lead_is_pinned_to_the_branch_in_the_token()
    {
        var response = await SendAsync("/read", nameof(UserRole.Lead), null, branchHeader: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await ReadPayloadAsync(response);
        payload.BranchId.ShouldBe(BranchOfA);
        payload.AllBranches.ShouldBeFalse();
    }

    // ---------------------------------------------------------------------
    // Organization Admin
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Organization_admin_without_the_header_reads_all_branches()
    {
        var response = await SendAsync("/read", nameof(UserRole.Admin), nameof(AdminScope.Organization), null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await ReadPayloadAsync(response);
        payload.BranchId.ShouldBeNull();
        payload.AllBranches.ShouldBeTrue();
        response.Headers.GetValues(TenancyOptions.EffectiveBranchHeaderName).Single()
            .ShouldBe(TenancyOptions.AllBranchesHeaderValue);
    }

    [Fact]
    public async Task Organization_admin_with_a_valid_header_is_narrowed_to_that_branch()
    {
        var response = await SendAsync("/read", nameof(UserRole.Admin), nameof(AdminScope.Organization), BranchOfA.ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await ReadPayloadAsync(response);
        payload.BranchId.ShouldBe(BranchOfA);
        payload.AllBranches.ShouldBeFalse();

        // API-027: the header lets a client confirm which branch was actually applied. It is a
        // diagnostic, not an authorization mechanism.
        response.Headers.GetValues(TenancyOptions.EffectiveBranchHeaderName).Single()
            .ShouldBe(BranchOfA.ToString());
    }

    /// <summary><c>TEST-TEN-012</c>: a branch-scoped mutation without a chosen branch.</summary>
    [Fact]
    public async Task Mutation_without_a_chosen_branch_returns_branch_context_required()
    {
        var response = await SendAsync("/mutate", nameof(UserRole.Admin), nameof(AdminScope.Organization), null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.BranchContextRequired);
    }

    [Fact]
    public async Task Mutation_with_a_chosen_branch_proceeds()
    {
        var response = await SendAsync("/mutate", nameof(UserRole.Admin), nameof(AdminScope.Organization), BranchOfA.ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadPayloadAsync(response)).BranchId.ShouldBe(BranchOfA);
    }

    /// <summary>
    /// <c>TEST-TEN-013</c>: 404, not 403. A 403 would confirm that a branch of another organization
    /// exists, turning the error into a reconnaissance channel (<c>TEN-007</c>).
    /// </summary>
    [Fact]
    public async Task Header_naming_a_branch_of_another_organization_returns_not_found()
    {
        var response = await SendAsync("/read", nameof(UserRole.Admin), nameof(AdminScope.Organization), BranchOfB.ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ResourceNotFound);
    }

    /// <summary>The response for a foreign branch is indistinguishable from one for a random id.</summary>
    [Fact]
    public async Task Foreign_branch_and_unknown_branch_are_indistinguishable()
    {
        var foreign = await SendAsync("/read", nameof(UserRole.Admin), nameof(AdminScope.Organization), BranchOfB.ToString());
        var unknown = await SendAsync("/read", nameof(UserRole.Admin), nameof(AdminScope.Organization), Guid.CreateVersion7().ToString());

        foreign.StatusCode.ShouldBe(unknown.StatusCode);

        static string WithoutTraceId(string body) =>
            System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "\"traceId\":\"*\"");

        WithoutTraceId(await foreign.Content.ReadAsStringAsync())
            .ShouldBe(WithoutTraceId(await unknown.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task A_malformed_header_value_is_a_validation_failure()
    {
        var response = await SendAsync("/read", nameof(UserRole.Admin), nameof(AdminScope.Organization), "not-a-uuid");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe(ErrorCodes.ValidationFailed);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(string path, string role, string? adminScope, string? branchHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthenticationMiddleware.RoleHeader, role);

        if (adminScope is not null)
        {
            request.Headers.Add(TestAuthenticationMiddleware.AdminScopeHeader, adminScope);
        }

        if (branchHeader is not null)
        {
            request.Headers.Add("X-MTF-Branch-Id", branchHeader);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    private static async Task<Payload> ReadPayloadAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<Payload>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed record Payload(Guid OrganizationId, Guid? BranchId, bool AllBranches);

    /// <summary>
    /// Stands in for the JWT authentication of Phase 2: builds the principal that a validated token
    /// would produce, so the scope rules can be exercised before the token pipeline exists.
    /// </summary>
    private sealed class TestAuthenticationMiddleware(RequestDelegate next)
    {
        public const string RoleHeader = "X-Test-Role";
        public const string AdminScopeHeader = "X-Test-Admin-Scope";

        public Task InvokeAsync(HttpContext context)
        {
            if (!context.Request.Headers.TryGetValue(RoleHeader, out var role))
            {
                return next(context);
            }

            var adminScope = context.Request.Headers.TryGetValue(AdminScopeHeader, out var scope)
                ? scope.ToString()
                : null;

            var isOrganizationAdmin = role == nameof(UserRole.Admin) && adminScope == nameof(AdminScope.Organization);
            var isAdmin = role == nameof(UserRole.Admin);

            var claims = new List<Claim>
            {
                new(MtfClaimTypes.Subject, Guid.CreateVersion7().ToString()),
                new(MtfClaimTypes.Role, role.ToString()),
                new(MtfClaimTypes.OrganizationId, OrganizationA.ToString()),
                new(MtfClaimTypes.TokenVersion, "0"),
            };

            if (adminScope is not null)
            {
                claims.Add(new Claim(MtfClaimTypes.AdminScope, adminScope));
            }

            // AUTH-031: a claim that does not apply to the role is not serialized at all.
            if (!isOrganizationAdmin)
            {
                claims.Add(new Claim(MtfClaimTypes.BranchId, BranchOfA.ToString()));
            }

            if (!isAdmin)
            {
                claims.Add(new Claim(MtfClaimTypes.CategoryId, Guid.CreateVersion7().ToString()));
            }

            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return next(context);
        }
    }

    private sealed class StubBranchScopeValidator : IBranchScopeValidator
    {
        public Task<bool> BelongsToOrganizationAsync(Guid branchId, Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(branchId == BranchOfA && organizationId == OrganizationA);
    }
}
