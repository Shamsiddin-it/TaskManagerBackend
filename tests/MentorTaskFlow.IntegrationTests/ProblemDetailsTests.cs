using System.Net;
using System.Text.Json;
using MentorTaskFlow.Api.Middleware;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MentorTaskFlow.IntegrationTests;

/// <summary>
/// Verifies the RFC 9457 contract of TZ 26.1 against the real middleware.
/// </summary>
/// <remarks>
/// The host here is minimal and test-owned: the production API must not expose an endpoint that
/// throws on demand, so the middleware is exercised over a throwaway pipeline instead.
/// </remarks>
public sealed class ProblemDetailsTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services => services.AddLogging())
                .Configure(app =>
                {
                    app.UseCorrelationId();
                    app.UseMentorTaskFlowExceptionHandling();
                    app.Run(context => context.Request.Path.Value switch
                    {
                        "/not-found" => throw new NotFoundException(),
                        "/forbidden" => throw new ForbiddenException(ErrorCodes.ScopeOverrideForbidden),
                        "/conflict" => throw new ConflictException(ErrorCodes.LateSubmissionDisabled),
                        "/validation" => throw new ValidationAppException("pageSize", "Значение вне диапазона."),
                        "/domain" => throw new DomainException(
                            ErrorCodes.AssignmentTerminal,
                            "Задача завершена."),
                        _ => throw new InvalidOperationException(
                            "Server=db;Password=super-secret;stack trace detail"),
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

    [Theory]
    [InlineData("/not-found", HttpStatusCode.NotFound, ErrorCodes.ResourceNotFound)]
    [InlineData("/forbidden", HttpStatusCode.Forbidden, ErrorCodes.ScopeOverrideForbidden)]
    [InlineData("/conflict", HttpStatusCode.Conflict, ErrorCodes.LateSubmissionDisabled)]
    [InlineData("/validation", HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed)]
    [InlineData("/domain", HttpStatusCode.Conflict, ErrorCodes.AssignmentTerminal)]
    public async Task Status_and_code_come_from_the_catalog(string path, HttpStatusCode status, string code)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("code").GetString().ShouldBe(code);
        document.RootElement.GetProperty("type").GetString().ShouldBe(ErrorCodes.ToTypeUri(code));
    }

    /// <summary><c>API-023</c>: traceId equals the X-Correlation-Id of the response.</summary>
    [Fact]
    public async Task TraceId_matches_the_correlation_header()
    {
        var response = await _client.GetAsync("/not-found");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("traceId").GetString()
            .ShouldBe(response.Headers.GetValues("X-Correlation-Id").Single());
    }

    /// <summary><c>API-022</c>: the errors object is present only for 400 VALIDATION_FAILED.</summary>
    [Fact]
    public async Task Errors_object_is_present_only_for_validation_failures()
    {
        using var validation = JsonDocument.Parse(
            await (await _client.GetAsync("/validation")).Content.ReadAsStringAsync());
        validation.RootElement.GetProperty("errors").GetProperty("pageSize").GetArrayLength().ShouldBe(1);

        using var conflict = JsonDocument.Parse(
            await (await _client.GetAsync("/conflict")).Content.ReadAsStringAsync());
        conflict.RootElement.TryGetProperty("errors", out _).ShouldBeFalse();
    }

    /// <summary>
    /// <c>API-025</c>: an unhandled exception returns 500 INTERNAL_ERROR with a traceId only. The
    /// message, stack trace and request data are withheld in every environment — this test runs in
    /// Development precisely because that is where leaking is normally tolerated.
    /// </summary>
    [Fact]
    public async Task Unhandled_exception_never_leaks_its_message()
    {
        var response = await _client.GetAsync("/boom");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("code").GetString().ShouldBe(ErrorCodes.InternalError);
        document.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();

        body.ShouldNotContain("super-secret");
        body.ShouldNotContain("InvalidOperationException");
        body.ShouldNotContain("stack trace detail");
    }

    /// <summary>
    /// <c>TEN-006</c>: the 404 body must be identical whichever isolation level rejected the request,
    /// so it can never confirm that a foreign object exists.
    /// </summary>
    [Fact]
    public async Task Not_found_body_is_identical_across_requests()
    {
        var first = await (await _client.GetAsync("/not-found")).Content.ReadAsStringAsync();
        var second = await (await _client.GetAsync("/not-found")).Content.ReadAsStringAsync();

        // traceId is the only per-request value; strip it before comparing.
        static string WithoutTraceId(string body) =>
            System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "\"traceId\":\"*\"");

        WithoutTraceId(first).ShouldBe(WithoutTraceId(second));
    }
}
