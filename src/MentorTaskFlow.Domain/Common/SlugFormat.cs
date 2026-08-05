using System.Text.RegularExpressions;

namespace MentorTaskFlow.Domain.Common;

/// <summary>
/// Format rules mirrored one-for-one by the CHECK constraints of TZ 12.2.
/// </summary>
/// <remarks>
/// The application check exists so the caller gets 400 <c>VALIDATION_FAILED</c> with a readable
/// message; the CHECK constraint exists so a value that bypasses the application is still refused.
/// Neither is sufficient alone (<c>TEN-023</c>), so the patterns must stay identical — the migration
/// that creates the constraints cites these fields.
/// </remarks>
public static partial class SlugFormat
{
    /// <summary><c>ck_organizations_slug_format</c>: <c>^[a-z0-9]+(-[a-z0-9]+)*$</c>, length 2–80.</summary>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    public const string Pattern = "^[a-z0-9]+(-[a-z0-9]+)*$";
    public const int MinLength = 2;
    public const int MaxLength = 80;

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length is >= MinLength and <= MaxLength
        && SlugPattern().IsMatch(value);
}

/// <summary>Branch code format: <c>ck_branches_code_format</c>.</summary>
public static partial class BranchCodeFormat
{
    /// <summary><c>^[A-Z0-9][A-Z0-9-]*$</c>, length 2–32.</summary>
    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public const string Pattern = "^[A-Z0-9][A-Z0-9-]*$";
    public const int MinLength = 2;
    public const int MaxLength = 32;

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length is >= MinLength and <= MaxLength
        && CodePattern().IsMatch(value);
}
