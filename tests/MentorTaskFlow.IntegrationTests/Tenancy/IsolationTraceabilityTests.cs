using System.Globalization;

namespace MentorTaskFlow.IntegrationTests.Tenancy;

/// <summary>
/// The acceptance gate of <c>TEST-013</c> and <c>TEST-014</c>: every isolation test case of 31.9
/// exists in the suite.
/// </summary>
/// <remarks>
/// <para>
/// 31.9 is normative — no <c>ORG-</c>, <c>BRN-</c> or <c>TEN-</c> requirement counts as implemented
/// without a passing test case, and Приложение J records the mapping. A list in a document decays
/// silently, so the mapping is checked mechanically: a case that nobody wrote fails here rather than
/// being noticed during acceptance.
/// </para>
/// <para>
/// The check scans test sources rather than reflecting over attributes. The identifiers already live
/// in the doc comments of the tests that cover them, and one case is often covered by several tests
/// across two assemblies; requiring an attribute as well would put the same fact in two places, and
/// the two would eventually disagree.
/// </para>
/// </remarks>
public sealed class IsolationTraceabilityTests
{
    /// <summary>
    /// <c>TEST-TEN-035</c> is frontend-only and is deliberately absent.
    /// </summary>
    /// <remarks>
    /// It asserts that switching the branch selector cancels in-flight requests and clears the
    /// branch-scoped cache (<c>FE-038</c>) — behaviour of the SPA, which is outside this repository.
    /// Its backend half is <c>TEST-TEN-036</c>, which <b>is</b> covered here: a mutation in
    /// all-branches mode answers 400 when called directly.
    /// </remarks>
    private static readonly IReadOnlySet<string> FrontendOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "TEST-TEN-035",
    };

    [Fact]
    public void Every_isolation_test_case_of_section_31_9_is_covered()
    {
        var sources = ReadTestSources();

        var missing = Enumerable.Range(1, 40)
            .Select(number => $"TEST-TEN-{number.ToString("D3", CultureInfo.InvariantCulture)}")
            .Where(id => !FrontendOnly.Contains(id))
            .Where(id => !sources.Contains(id, StringComparison.Ordinal))
            .ToArray();

        missing.ShouldBeEmpty(
            $"31.9 requires a passing test for each case; missing: {string.Join(", ", missing)}");
    }

    /// <summary>The security cases of 31.4 that the plan names for this phase.</summary>
    [Fact]
    public void The_architecture_security_cases_are_covered()
    {
        var sources = ReadTestSources();

        foreach (var id in new[] { "TEST-SEC-021", "TEST-SEC-022", "TEST-SEC-023" })
        {
            sources.ShouldContain(id, Case.Sensitive, $"{id} has no test.");
        }
    }

    /// <summary>
    /// Concatenates every test source of the solution.
    /// </summary>
    /// <remarks>
    /// The repository root is found by walking up from the test assembly until the solution file
    /// appears, so the check works from any working directory and from CI's checkout path alike.
    /// </remarks>
    private static string ReadTestSources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MentorTaskFlow.sln")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("MentorTaskFlow.sln was not found above the test assembly.");

        var tests = Path.Combine(directory!.FullName, "tests");

        // `obj` and `bin` hold generated copies of the same files; counting them would let a stale
        // build satisfy the gate after the test itself had been deleted.
        var files = Directory
            .EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.EndsWith(nameof(IsolationTraceabilityTests) + ".cs", StringComparison.Ordinal));

        return string.Join('\n', files.Select(File.ReadAllText));
    }
}
