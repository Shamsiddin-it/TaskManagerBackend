namespace MentorTaskFlow.Application.Common.Abstractions;

/// <summary>
/// Request metadata the AuditLog records (TZ 10.14).
/// </summary>
/// <remarks>
/// <para>
/// An abstraction rather than <c>HttpContext</c> so Infrastructure stays free of ASP.NET: the same
/// writer serves background tasks, which have no request at all, and the layering test can keep
/// asserting that persistence code does not reach for the web stack.
/// </para>
/// <para>
/// Every member is nullable. A background task legitimately has none of them, and a null is the
/// honest answer rather than a fabricated placeholder.
/// </para>
/// </remarks>
public interface IRequestContext
{
    /// <summary>Shared by the AuditLog row, the technical log and any notifications (<c>AUD-006</c>).</summary>
    Guid CorrelationId { get; }

    string? HttpMethod { get; }

    /// <summary>Without the query string, which may carry a token, code or signature (<c>SEC-024</c>).</summary>
    string? Path { get; }

    /// <summary>Personal data; retention nulls it after 90 days (TZ 27.5).</summary>
    string? IpAddress { get; }

    string? UserAgent { get; }
}
