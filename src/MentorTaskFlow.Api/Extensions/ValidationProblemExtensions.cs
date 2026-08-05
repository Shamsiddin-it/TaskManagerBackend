using MentorTaskFlow.Contracts.Common;
using Microsoft.AspNetCore.Mvc;

namespace MentorTaskFlow.Api.Extensions;

/// <summary>
/// Rewrites the framework's default model-binding 400 into the project's ProblemDetails shape so that
/// binding failures and hand-thrown validation failures are indistinguishable to clients.
/// </summary>
/// <remarks>
/// Without this, a request rejected by strict deserialization (<c>API-005</c>) would return a
/// <c>ValidationProblemDetails</c> without the stable <c>code</c> field that the frontend and the
/// test suite rely on (<c>API-021</c>, <c>API-022</c>).
/// </remarks>
public static class ValidationProblemExtensions
{
    public static IMvcBuilder AddMentorTaskFlowValidationProblem(this IMvcBuilder builder)
    {
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(entry => entry.Value?.Errors.Count > 0)
                    .ToDictionary(
                        entry => entry.Key,
                        entry => entry.Value!.Errors
                            .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                                ? "Значение недопустимо."
                                : error.ErrorMessage)
                            .ToArray());

                var problem = new ProblemDetails
                {
                    Type = ErrorCodes.ToTypeUri(ErrorCodes.ValidationFailed),
                    Title = "Validation failed",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "Запрос не прошёл валидацию.",
                    Instance = context.HttpContext.Request.Path.Value,
                };

                problem.Extensions["code"] = ErrorCodes.ValidationFailed;
                problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
                problem.Extensions["errors"] = errors;

                return new BadRequestObjectResult(problem)
                {
                    ContentTypes = { "application/problem+json" },
                };
            };
        });

        return builder;
    }
}
