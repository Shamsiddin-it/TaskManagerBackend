using System.Reflection;
using MentorTaskFlow.Contracts.Common;

namespace MentorTaskFlow.UnitTests.Common;

/// <summary>
/// Guards the error catalog against drift from Приложение C.
/// </summary>
/// <remarks>
/// The catalog is a contract, not an implementation detail: the frontend switches on <c>code</c> and
/// the test suite asserts on it (<c>API-021</c>). A silently added, removed or re-pointed code breaks
/// consumers without breaking compilation — hence these tests.
/// </remarks>
public sealed class ErrorCatalogTests
{
    /// <summary>Приложение C: «Версия 2.1 определяла 44 кода… всего 54 кода».</summary>
    private const int ExpectedCodeCount = 54;

    private static IReadOnlyList<string> DeclaredCodes() =>
    [
        .. typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
    ];

    [Fact]
    public void Catalog_contains_exactly_the_54_codes_of_appendix_C()
    {
        ErrorCodes.StatusByCode.Count.ShouldBe(ExpectedCodeCount);
    }

    [Fact]
    public void Every_declared_constant_has_a_status_mapping()
    {
        var declared = DeclaredCodes();

        declared.Count.ShouldBe(ExpectedCodeCount);

        foreach (var code in declared)
        {
            ErrorCodes.StatusByCode.ShouldContainKey(code);
        }
    }

    [Fact]
    public void Codes_are_unique()
    {
        var declared = DeclaredCodes();

        declared.Distinct(StringComparer.Ordinal).Count().ShouldBe(declared.Count);
    }

    [Fact]
    public void Codes_use_screaming_snake_case()
    {
        foreach (var code in ErrorCodes.StatusByCode.Keys)
        {
            code.ShouldMatch("^[A-Z][A-Z0-9]*(_[A-Z0-9]+)*$");
        }
    }

    [Fact]
    public void Statuses_are_known_http_error_codes()
    {
        int[] allowed = [400, 401, 403, 404, 409, 413, 415, 422, 429, 500, 503];

        foreach (var (code, status) in ErrorCodes.StatusByCode)
        {
            allowed.ShouldContain(status, $"{code} maps to an unexpected status {status}.");
        }
    }

    /// <summary>
    /// <c>TEN-006</c>/<c>TEN-007</c>: a code that distinguishes «foreign» from «missing» would confirm
    /// the existence of an object outside the caller's scope and turn an error into a reconnaissance
    /// channel. Приложение C states these codes «отсутствуют и не могут быть добавлены».
    /// </summary>
    [Fact]
    public void Codes_that_would_disclose_foreign_objects_are_absent()
    {
        foreach (var forbidden in ErrorCodes.PermanentlyForbiddenCodes)
        {
            ErrorCodes.StatusByCode.ShouldNotContainKey(forbidden);
        }
    }

    /// <summary>All five isolation cases of TZ 9.2 collapse into one 404 code.</summary>
    [Fact]
    public void Single_not_found_code_covers_every_isolation_case()
    {
        ErrorCodes.StatusByCode
            .Where(pair => pair.Value == 404)
            .Select(pair => pair.Key)
            .ShouldBe([ErrorCodes.ResourceNotFound]);
    }

    [Theory]
    [InlineData(ErrorCodes.LateSubmissionDisabled, "https://mentortaskflow/errors/late-submission-disabled")]
    [InlineData(ErrorCodes.ResourceNotFound, "https://mentortaskflow/errors/resource-not-found")]
    [InlineData(ErrorCodes.ScopeOverrideForbidden, "https://mentortaskflow/errors/scope-override-forbidden")]
    public void Type_uri_matches_the_documented_shape(string code, string expected)
    {
        ErrorCodes.ToTypeUri(code).ShouldBe(expected);
    }
}
