namespace MentorTaskFlow.Domain.Common;

/// <summary>
/// Raised by a domain method when an invariant or a state transition is violated.
/// </summary>
/// <remarks>
/// The domain deliberately carries the stable error code rather than an HTTP status: mapping a code
/// to a status is a transport concern owned by the API layer. The domain never references
/// <c>MentorTaskFlow.Contracts</c> — the code is passed as a plain string and validated against the
/// catalog by <c>ErrorCatalogTests</c>.
/// </remarks>
public class DomainException : Exception
{
    public string Code { get; }

    public IReadOnlyDictionary<string, object?> Details { get; }

    public DomainException(string code, string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(message)
    {
        Code = code;
        Details = details ?? new Dictionary<string, object?>();
    }
}
