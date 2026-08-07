using MentorTaskFlow.Application.Common.Abstractions;

namespace MentorTaskFlow.Api.Middleware;

/// <summary>
/// Supplies <see cref="IRequestContext"/> from the current HTTP request.
/// </summary>
/// <remarks>
/// The correlation id is the one <see cref="CorrelationIdMiddleware"/> established, so the AuditLog
/// row, the technical log lines and the notifications of one action share a single identifier and an
/// incident can be reconstructed from any of them (<c>AUD-006</c>, <c>API-007</c>).
/// </remarks>
public sealed class HttpRequestContext(IHttpContextAccessor httpContextAccessor) : IRequestContext
{
    public Guid CorrelationId =>
        Guid.TryParse(httpContextAccessor.HttpContext?.TraceIdentifier, out var correlationId)
            ? correlationId
            : Guid.Empty;

    public string? HttpMethod => httpContextAccessor.HttpContext?.Request.Method;

    /// <summary>
    /// Path only. The query string is excluded because it may carry <c>token</c>, <c>code</c> or a
    /// storage signature, none of which may reach the AuditLog (<c>SEC-024</c>, <c>AUD-022</c>).
    /// </summary>
    public string? Path => httpContextAccessor.HttpContext?.Request.Path.Value;

    public string? IpAddress => httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent
    {
        get
        {
            var userAgent = httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString();
            return string.IsNullOrEmpty(userAgent) ? null : userAgent;
        }
    }
}

/// <summary>
/// Stands in for <see cref="HttpRequestContext"/> in background tasks, which have no request.
/// </summary>
/// <remarks>
/// Every member is null except the correlation id, which is generated per instance so a job's audit
/// rows and notifications can still be tied together.
/// </remarks>
public sealed class BackgroundRequestContext : IRequestContext
{
    public Guid CorrelationId { get; } = Guid.CreateVersion7();

    public string? HttpMethod => null;

    public string? Path => null;

    public string? IpAddress => null;

    public string? UserAgent => null;
}
