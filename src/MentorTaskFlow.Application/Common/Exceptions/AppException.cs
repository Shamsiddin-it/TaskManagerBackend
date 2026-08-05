using MentorTaskFlow.Contracts.Common;

namespace MentorTaskFlow.Application.Common.Exceptions;

/// <summary>
/// Base for every deliberate application-level failure. Carries a stable code from
/// <see cref="ErrorCodes"/>; the HTTP status is derived from the catalog, never passed by the caller.
/// </summary>
public abstract class AppException(string code, string message, IReadOnlyDictionary<string, object?>? details = null)
    : Exception(message)
{
    public string Code { get; } = code;

    public IReadOnlyDictionary<string, object?> Details { get; } = details ?? new Dictionary<string, object?>();
}

/// <summary>
/// 404. Used for a missing object <b>and</b> for an object belonging to another Organization, Branch,
/// Category or Mentor — the responses are byte-identical and do not disclose which case occurred
/// (<c>TEN-006</c>, TZ 9.2).
/// </summary>
public sealed class NotFoundException(string? detail = null)
    : AppException(ErrorCodes.ResourceNotFound, detail ?? "Запрошенный объект не найден.");

/// <summary>401.</summary>
public sealed class UnauthorizedException(string code = ErrorCodes.Unauthorized, string? detail = null)
    : AppException(code, detail ?? "Требуется аутентификация.");

/// <summary>
/// 403. The object <b>is</b> within the caller's visibility, but their role or admin scope does not
/// permit the action (TZ 9.3). If the object is outside visibility, throw <see cref="NotFoundException"/>.
/// </summary>
public sealed class ForbiddenException(string code = ErrorCodes.Forbidden, string? detail = null)
    : AppException(code, detail ?? "Действие запрещено для текущей роли.");

/// <summary>409. The caller may perform the action, but the current resource state forbids it (TZ 9.4).</summary>
public sealed class ConflictException(string code, string? detail = null, IReadOnlyDictionary<string, object?>? details = null)
    : AppException(code, detail ?? "Текущее состояние ресурса не допускает операцию.", details);

/// <summary>
/// 400. Request body or parameters are syntactically wrong, out of range, or carry an unknown member
/// (<c>API-005</c>). <see cref="Errors"/> maps field name → messages and is surfaced in the
/// ProblemDetails <c>errors</c> object (<c>API-022</c>).
/// </summary>
public sealed class ValidationAppException : AppException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationAppException(IReadOnlyDictionary<string, string[]> errors, string? detail = null)
        : base(ErrorCodes.ValidationFailed, detail ?? "Запрос не прошёл валидацию.")
        => Errors = errors;

    public ValidationAppException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }
}
