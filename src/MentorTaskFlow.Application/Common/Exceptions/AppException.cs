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

/// <summary>
/// 400 <c>BRANCH_CONTEXT_REQUIRED</c>: an Organization Admin attempted a branch-scoped mutation
/// without choosing a branch (<c>TEN-033</c>).
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="ValidationAppException"/>: nothing about the payload is wrong, so
/// there is no field to report. The caller has the right to act — they have simply not said where,
/// and no endpoint changes more than one branch per request, so there is no default to assume
/// (<c>TEN-034</c>).
/// </remarks>
/// <summary>
/// 400 <c>SECURITY_TOKEN_INVALID</c>: a set-password or reset-password token is unknown, expired,
/// already used or superseded (<c>AUTH-016</c>, <c>AUTH-017</c>).
/// </summary>
/// <remarks>
/// One code for all four cases on purpose. Telling a caller «this token existed but expired» apart
/// from «no such token» would confirm that a guessed value was otherwise real, and the message is
/// shown to someone who, by definition, has not yet proven who they are.
/// </remarks>
public sealed class SecurityTokenInvalidException()
    : AppException(
        ErrorCodes.SecurityTokenInvalid,
        "Ссылка недействительна или срок её действия истёк.");

public sealed class BranchContextRequiredException()
    : AppException(
        ErrorCodes.BranchContextRequired,
        "Не выбран филиал для операции. Передайте заголовок X-MTF-Branch-Id.");

/// <summary>
/// 413. The upload exceeded the size limit (<c>SUB-012</c>).
/// </summary>
/// <remarks>
/// Raised on bytes actually read, not on <c>Content-Length</c>: a client controls its own headers, so
/// a limit checked only against the declared length is not a limit.
/// </remarks>
public sealed class PayloadTooLargeException(string code, string? detail = null)
    : AppException(code, detail ?? "Размер запроса превышает допустимый.");

/// <summary>415. The extension or declared MIME type is outside the allowlist (<c>SUB-011</c>).</summary>
public sealed class UnsupportedMediaTypeException(string code, string? detail = null)
    : AppException(code, detail ?? "Тип файла не поддерживается.");

/// <summary>
/// 422. The file arrived intact but is not what it claims to be — a broken signature, an OPC container
/// missing its parts, or an archive breaching the safety limits (17.3).
/// </summary>
public sealed class UnprocessableEntityException(string code, string? detail = null)
    : AppException(code, detail ?? "Содержимое файла не прошло проверку.");

/// <summary>503. A dependency the request cannot proceed without is unreachable (<c>REL-008</c>).</summary>
public sealed class ServiceUnavailableException(string code, string? detail = null)
    : AppException(code, detail ?? "Сервис временно недоступен.");

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

    /// <summary>
    /// Reports several violations for one field at once.
    /// </summary>
    /// <remarks>
    /// Used by the password policy: surfacing one broken rule at a time turns setting a password into
    /// a guessing game where every attempt reveals one more requirement (<c>AUTH-013</c>).
    /// </remarks>
    public ValidationAppException(string field, IReadOnlyCollection<string> messages)
        : this(new Dictionary<string, string[]> { [field] = [.. messages] })
    {
    }
}
