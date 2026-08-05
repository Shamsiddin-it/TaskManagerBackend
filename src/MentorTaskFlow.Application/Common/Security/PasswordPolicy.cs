using MentorTaskFlow.Application.Common.Exceptions;

namespace MentorTaskFlow.Application.Common.Security;

/// <summary>
/// Supplies the list of passwords considered too common to allow (<c>AUTH-013</c>).
/// </summary>
public interface ICommonPasswordCatalog
{
    /// <summary>Case-insensitive membership test.</summary>
    bool Contains(string password);
}

/// <summary>
/// The password rules of <c>AUTH-013</c>: 12–128 characters, at least one digit and one uppercase
/// letter, and not present in the common-password list.
/// </summary>
/// <remarks>
/// No further composition requirements are imposed, and that is a deliberate reading of the TZ:
/// forcing punctuation classes pushes users toward predictable substitutions (<c>P@ssw0rd!</c>) that
/// the common-password check catches anyway, while length does the real work.
/// </remarks>
public sealed class PasswordPolicy(ICommonPasswordCatalog commonPasswords)
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    /// <summary>Throws <see cref="ValidationAppException"/> describing every rule the password breaks.</summary>
    public void Validate(string? password, string fieldName = "newPassword")
    {
        var errors = Evaluate(password).ToArray();

        if (errors.Length > 0)
        {
            throw new ValidationAppException(fieldName, errors);
        }
    }

    /// <summary>
    /// Returns every violated rule rather than the first.
    /// </summary>
    /// <remarks>
    /// Reporting them one at a time turns setting a password into a guessing game where each attempt
    /// reveals one more requirement.
    /// </remarks>
    public IEnumerable<string> Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            yield return $"Пароль обязателен и должен содержать от {MinLength} до {MaxLength} символов.";
            yield break;
        }

        if (password.Length < MinLength || password.Length > MaxLength)
        {
            yield return $"Пароль должен содержать от {MinLength} до {MaxLength} символов.";
        }

        if (!password.Any(char.IsDigit))
        {
            yield return "Пароль должен содержать хотя бы одну цифру.";
        }

        if (!password.Any(char.IsUpper))
        {
            yield return "Пароль должен содержать хотя бы одну заглавную букву.";
        }

        if (commonPasswords.Contains(password))
        {
            yield return "Этот пароль входит в список наиболее распространённых и не может быть использован.";
        }
    }
}
