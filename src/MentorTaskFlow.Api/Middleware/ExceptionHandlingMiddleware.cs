using System.Text.Json;
using MentorTaskFlow.Application.Common.Exceptions;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Middleware;

/// <summary>
/// Converts every failure into an RFC 9457 ProblemDetails response with a stable <c>code</c> field
/// (TZ 26.1).
/// </summary>
/// <remarks>
/// <para>
/// An unhandled exception becomes 500 <c>INTERNAL_ERROR</c> carrying only a <c>traceId</c>. The
/// exception message, stack trace and request data are never returned — in any environment,
/// Development included (<c>API-025</c>).
/// </para>
/// <para>
/// 401/403/404 bodies carry no hint about the existence or owner of an object (<c>API-024</c>):
/// this is what keeps the five distinct 404 cases of <c>TEN-006</c> indistinguishable.
/// </para>
/// </remarks>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            await HandleAsync(context, exception);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted)
        {
            // The status line is already on the wire; overwriting it would produce a malformed
            // response. Log and let the connection fail.
            logger.LogError(exception, "Unhandled exception after the response had started.");
            throw exception;
        }

        var problem = BuildProblem(context, exception);

        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception: {ErrorCode}", problem.Extensions["code"]);
        }
        else
        {
            // Expected failures — validation, conflicts, isolation rejections. Warning, not Error
            // (OBS-002): a Mentor hitting a foreign object is normal traffic, not an incident.
            logger.LogWarning(
                "Request failed with {ErrorCode} ({StatusCode}).",
                problem.Extensions["code"],
                problem.Status);
        }

        context.Response.Clear();
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static ProblemDetails BuildProblem(HttpContext context, Exception exception)
    {
        var (code, detail, errors, extra) = exception switch
        {
            ValidationAppException validation =>
                (validation.Code, validation.Message, validation.Errors, validation.Details),

            AppException app =>
                (app.Code, app.Message, null, app.Details),

            DomainException domain =>
                (domain.Code, domain.Message, null, domain.Details),

            OperationCanceledException when context.RequestAborted.IsCancellationRequested =>
                (ErrorCodes.InternalError, "Запрос отменён клиентом.", null, EmptyDetails),

            _ => (ErrorCodes.InternalError, "Внутренняя ошибка сервера.", null, EmptyDetails),
        };

        var status = ErrorCodes.StatusByCode.TryGetValue(code, out var mapped)
            ? mapped
            : StatusCodes.Status500InternalServerError;

        // The detail of a 500 must not leak the exception text (API-025).
        var safeDetail = status >= StatusCodes.Status500InternalServerError
            ? "Внутренняя ошибка сервера."
            : detail;

        var problem = new ProblemDetails
        {
            Type = ErrorCodes.ToTypeUri(code),
            Title = ToTitle(code),
            Status = status,
            Detail = safeDetail,
            Instance = context.Request.Path.Value,
        };

        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = context.TraceIdentifier;

        // `errors` is populated only for 400 VALIDATION_FAILED (API-022).
        if (errors is not null)
        {
            problem.Extensions["errors"] = errors;
        }

        foreach (var (key, value) in extra)
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyDetails = new Dictionary<string, object?>();

    /// <summary>Renders <c>LATE_SUBMISSION_DISABLED</c> as <c>Late submission disabled</c>.</summary>
    private static string ToTitle(string code)
    {
        var words = code.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return code;
        }

        var result = string.Join(' ', words.Select(w => w.ToLowerInvariant()));
        return char.ToUpperInvariant(result[0]) + result[1..];
    }
}

public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseMentorTaskFlowExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<ExceptionHandlingMiddleware>();
}
