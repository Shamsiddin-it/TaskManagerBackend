using System.Reflection;
using MentorTaskFlow.Contracts.Common;
using MentorTaskFlow.Domain.Common;

namespace MentorTaskFlow.UnitTests.Common;

/// <summary>
/// Keeps <see cref="DomainErrorCodes"/> honest.
/// </summary>
/// <remarks>
/// The Domain project references nothing, so it duplicates the code literals it raises. Without this
/// test a typo or an invented code would compile, reach production, and surface as a 500 because
/// <c>ErrorCodes.StatusByCode</c> would not recognise it.
/// </remarks>
public sealed class DomainErrorCodeTests
{
    private static IReadOnlyList<string> DomainCodes() =>
    [
        .. typeof(DomainErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
    ];

    [Fact]
    public void Every_domain_code_exists_in_the_contract_catalog()
    {
        foreach (var code in DomainCodes())
        {
            ErrorCodes.StatusByCode.ShouldContainKey(
                code,
                $"{code} is raised by the domain but is absent from Приложение C.");
        }
    }

    [Fact]
    public void No_domain_code_is_a_permanently_forbidden_one()
    {
        foreach (var code in DomainCodes())
        {
            ErrorCodes.PermanentlyForbiddenCodes.ShouldNotContain(code);
        }
    }
}
