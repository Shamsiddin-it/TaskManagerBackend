using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MentorTaskFlow.Contracts.Auth;
using MentorTaskFlow.Domain.Categories;
using MentorTaskFlow.Domain.Tenancy;
using MentorTaskFlow.Domain.Users;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.Infrastructure.Observability;
using MentorTaskFlow.IntegrationTests.Persistence;

namespace MentorTaskFlow.IntegrationTests.Observability;

/// <summary>
/// The Prometheus endpoint of <c>OBS-007</c> and the label discipline of <c>OBS-010</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>TEST-SEC-023</c> is asserted against a real scrape rather than against the code that emits the
/// instruments. The rule is about what leaves the process — a label added by a package, by the
/// framework, or by a helper nobody audited is exactly the case a source-level check would miss.
/// </para>
/// <para>
/// The endpoint is unauthenticated, which is why the check matters at all: a label carrying an email
/// or a user id would be readable by anything that can reach the port, and an unbounded label would
/// let an anonymous caller grow the collector's memory one probed URL at a time.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed partial class MetricsEndpointTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ValidPassword = "Karimov2026Task";
    private const string Zone = "Asia/Dushanbe";

    private static readonly DateTimeOffset Seeded = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private MentorTaskFlowApiFactory _factory = null!;
    private Guid _mentorId;

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

    /// <summary>
    /// <c>TEST-SEC-023</c>: no exported series carries a high-cardinality or identifying label.
    /// </summary>
    [Fact]
    public async Task No_exported_metric_uses_a_forbidden_label()
    {
        var scrape = await ScrapeAfterTrafficAsync();

        var labels = LabelNamesOf(scrape);

        labels.ShouldNotBeEmpty("the scrape carried no labelled series, so the check proved nothing");

        foreach (var label in labels)
        {
            MetricLabels.Forbidden.ShouldNotContain(label, $"label '{label}' is forbidden by OBS-010.");
        }
    }

    /// <summary>
    /// The stronger half of the same rule: labels are an allowlist, not a blocklist.
    /// </summary>
    /// <remarks>
    /// A blocklist only catches the identifiers somebody thought of. The set that is actually needed
    /// by 30.2 and 30.4 is small and known, so anything outside it is a decision that deserves to be
    /// made deliberately — which is what a failing test here forces.
    /// </remarks>
    [Fact]
    public async Task Every_exported_label_is_on_the_allowlist()
    {
        var scrape = await ScrapeAfterTrafficAsync();

        var unexpected = LabelNamesOf(scrape)
            .Where(label => !MetricLabels.Allowed.Contains(label))

            // Emitted by the exporter and the runtime rather than by the application, and bounded:
            // they name the process, the .NET version and the GC's own structures, not a tenant.
            // `le` is not a label the application chooses at all — it is the histogram bucket
            // boundary the Prometheus exposition format defines.
            .Where(label => label is not ("le" or "version" or "dotnet_version" or "state" or "generation" or "area" or "type" or "kind" or "gc_reason" or "gc_heap"))
            .ToArray();

        unexpected.ShouldBeEmpty(
            $"labels outside MetricLabels.Allowed: {string.Join(", ", unexpected)}");
    }

    /// <summary>
    /// <c>OBS-010</c> again, from the other side: the route label is a template, not a path.
    /// </summary>
    /// <remarks>
    /// This is the specific failure the rule exists for. Labelling by path would create one series per
    /// assignment, and the identifiers of every object anybody requested would sit on an
    /// unauthenticated endpoint.
    /// </remarks>
    [Fact]
    public async Task The_route_label_carries_no_identifier()
    {
        var scrape = await ScrapeAfterTrafficAsync();

        scrape.ShouldContain("http_requests_total");
        scrape.ShouldNotContain(_mentorId.ToString());

        // The template, with the parameter still a parameter.
        scrape.ShouldContain("api/v1/users/{id");
    }

    /// <summary><c>OBS-007</c>: the minimum metric surface is actually exported.</summary>
    [Theory]
    [InlineData("http_requests_total")]
    [InlineData("http_request_duration_seconds")]
    [InlineData("active_branches_total")]
    [InlineData("inactive_branches_total")]
    [InlineData("users_total")]
    public async Task The_required_series_are_exported(string series)
    {
        (await ScrapeAfterTrafficAsync()).ShouldContain(series);
    }

    /// <summary>
    /// <c>OBS-007</c>: the endpoint answers on the internal network and is unauthenticated there.
    /// </summary>
    /// <remarks>
    /// Unauthenticated is deliberate — a collector holds no token — which is precisely why the network
    /// allowlist is the boundary and why the label rules above are not optional.
    /// </remarks>
    [Fact]
    public async Task The_endpoint_needs_no_token()
    {
        using var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync("/metrics")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    /// <summary>Drives a little traffic, then scrapes — an empty registry would prove nothing.</summary>
    private async Task<string> ScrapeAfterTrafficAsync()
    {
        using var admin = await SignInAsync("organization-admin@mentortaskflow.test");

        await admin.GetAsync($"/api/v1/users/{_mentorId}");
        await admin.GetAsync("/api/v1/users?page=1&pageSize=20");
        await admin.GetAsync($"/api/v1/users/{Guid.CreateVersion7()}");

        using var anonymous = _factory.CreateClient();

        return await (await anonymous.GetAsync("/metrics")).Content.ReadAsStringAsync();
    }

    /// <summary>Every distinct label name in a Prometheus exposition body.</summary>
    private static IReadOnlySet<string> LabelNamesOf(string scrape) =>
        LabelBlock().Matches(scrape)
            .SelectMany(match => LabelName().Matches(match.Groups[1].Value))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^[a-zA-Z_:][a-zA-Z0-9_:]*\{([^}]*)\}", RegexOptions.Multiline)]
    private static partial Regex LabelBlock();

    [GeneratedRegex(@"([a-zA-Z_][a-zA-Z0-9_]*)=""")]
    private static partial Regex LabelName();

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, ValidPassword));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var login = JsonSerializer.Deserialize<LoginResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);

        return client;
    }

    private async Task SeedAsync()
    {
        await using var context = fixture.CreateContext(suppressTenantFilter: true);
        var passwordHash = new Pbkdf2PasswordHasher().Hash(ValidPassword);

        var organization = Organization.Provision("SoftClub Academy", "softclub-academy", Seeded);
        context.Organizations.Add(organization);

        var headOffice = Branch.CreateHeadOffice(organization.Id, "Главный офис", "HQ", null, Zone, Seeded);
        var khujand = Branch.Create(organization.Id, "Филиал Худжанд", "KHJ", null, Zone, Seeded);
        khujand.Deactivate(Seeded.AddDays(1));
        context.Branches.AddRange(headOffice, khujand);

        var sharp = Category.Create(organization.Id, headOffice.Id, "C#", null, Seeded);
        context.Categories.Add(sharp);
        context.CategorySettings.Add(CategorySettings.CreateDefault(sharp, Zone, Seeded));

        var users = new List<User>
        {
            User.CreateOrganizationAdmin(organization.Id, "Иван Каримов", "organization-admin@mentortaskflow.test", Seeded),
            User.CreateMentor(organization.Id, headOffice.Id, sharp.Id, "Ментор", "mentor@mentortaskflow.test", Seeded),
        };

        foreach (var user in users)
        {
            user.SetPasswordHash(passwordHash, Seeded);
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        _mentorId = users.Single(u => u.Email == "mentor@mentortaskflow.test").Id;
    }
}
