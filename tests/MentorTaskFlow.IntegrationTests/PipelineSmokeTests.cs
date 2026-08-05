using System.Net;
using System.Text.Json;

namespace MentorTaskFlow.IntegrationTests;

/// <summary>
/// Phase 0 acceptance: the empty skeleton boots and the cross-cutting pipeline behaves as specified.
/// </summary>
public sealed class PipelineSmokeTests(MentorTaskFlowApiFactory factory) : IClassFixture<MentorTaskFlowApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    /// <summary>
    /// <c>OBS-003</c>: liveness must not touch a dependency. The factory points the connection string
    /// at an unreachable host precisely so that a green result here proves independence.
    /// </summary>
    [Fact]
    public async Task Liveness_is_healthy_without_any_dependency()
    {
        var response = await _client.GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().ShouldBe("Healthy");
        document.RootElement.GetProperty("checks").GetArrayLength().ShouldBe(0);
    }

    /// <summary><c>OBS-004</c>: readiness reports Unhealthy while PostgreSQL is unreachable.</summary>
    [Fact]
    public async Task Readiness_is_unhealthy_when_postgres_is_unreachable()
    {
        var response = await _client.GetAsync("/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().ShouldBe("Unhealthy");
    }

    /// <summary>
    /// The readiness body is reachable anonymously, so it must not describe the failure. Exception
    /// text would disclose host, port and database name (<c>OBS-006</c>, <c>SEC-021</c>).
    /// </summary>
    [Fact]
    public async Task Readiness_body_does_not_leak_infrastructure_details()
    {
        var response = await _client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        body.ShouldNotContain("127.0.0.1");
        body.ShouldNotContain("Password");
        body.ShouldNotContain("Npgsql");
        body.ShouldNotContain("Exception");
    }

    /// <summary><c>API-007</c>: every response carries a correlation id.</summary>
    [Fact]
    public async Task Response_carries_a_generated_correlation_id()
    {
        var response = await _client.GetAsync("/health/live");

        response.Headers.TryGetValues("X-Correlation-Id", out var values).ShouldBeTrue();
        Guid.TryParse(values!.Single(), out _).ShouldBeTrue();
    }

    /// <summary><c>API-007</c>: an inbound correlation id is reused so callers can stitch a chain.</summary>
    [Fact]
    public async Task Inbound_correlation_id_is_echoed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "caller-supplied-id");

        var response = await _client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").Single().ShouldBe("caller-supplied-id");
    }

    /// <summary>
    /// A correlation id reaches the logs. A value containing a newline could forge log records, so a
    /// malformed inbound header is replaced rather than trusted.
    /// </summary>
    [Fact]
    public async Task Malformed_inbound_correlation_id_is_replaced()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "spoofed\r\ninjected: value");

        var response = await _client.SendAsync(request);

        var echoed = response.Headers.GetValues("X-Correlation-Id").Single();
        echoed.ShouldNotContain("injected");
        Guid.TryParse(echoed, out _).ShouldBeTrue();
    }

    /// <summary><c>SEC-009</c>: the security headers are present on every response.</summary>
    [Theory]
    [InlineData("X-Content-Type-Options", "nosniff")]
    [InlineData("X-Frame-Options", "DENY")]
    [InlineData("Referrer-Policy", "strict-origin-when-cross-origin")]
    public async Task Security_headers_are_applied(string header, string expected)
    {
        var response = await _client.GetAsync("/health/live");

        response.Headers.GetValues(header).Single().ShouldBe(expected);
    }

    [Fact]
    public async Task Content_security_policy_denies_everything_by_default()
    {
        var response = await _client.GetAsync("/health/live");

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.ShouldContain("default-src 'none'");
        csp.ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Openapi_document_is_served_in_development()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("info").GetProperty("title").GetString().ShouldBe("MentorTaskFlow API");
    }
}
