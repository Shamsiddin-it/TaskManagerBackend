using NetArchTest.Rules;

namespace MentorTaskFlow.ArchitectureTests;

/// <summary>
/// Turns a NetArchTest result into an assertion that names the offending types.
/// </summary>
/// <remarks>
/// A bare «IsSuccessful was false» tells whoever broke the rule nothing about what to fix, which is
/// how architecture tests end up being suppressed instead of honoured.
/// </remarks>
internal static class ArchTestResultAssertions
{
    public static void ShouldPass(this TestResult result, string rule)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        var offenders = result.FailingTypeNames ?? [];
        throw new ShouldAssertException(
            $"Architecture rule violated: {rule}{Environment.NewLine}" +
            $"Offending types:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", offenders));
    }
}
