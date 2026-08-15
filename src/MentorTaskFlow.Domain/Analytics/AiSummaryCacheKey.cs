using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MentorTaskFlow.Domain.Analytics;

/// <summary>
/// The cache key of <c>AI-009</c>.
/// </summary>
/// <remarks>
/// <para>
/// Built in one place because every component of it is load-bearing. The tenant scope in particular is
/// not decoration: drop <c>organizationId</c> and <c>branchId</c> and the category <c>C#</c> of the
/// head office and the category <c>C#</c> of Khujand share a key for the same period, so the second
/// request is answered with the first branch's report (<c>TEN-076</c>). That failure has no error and
/// no log line — it looks exactly like a cache hit.
/// </para>
/// <para>
/// The components are joined with a separator rather than concatenated as the formula is written. The
/// formula mixes fixed-width GUIDs with variable-width literals (<c>NONE</c>, <c>ALL_BRANCHES</c>) and
/// with dates, and unseparated concatenation of variable-width parts is precisely how two different
/// inputs come to share a key — which is the collision this key exists to prevent.
/// </para>
/// </remarks>
public static class AiSummaryCacheKey
{
    /// <summary>Bumped whenever the metric definitions of 21.2 change, so old reports fall out of cache.</summary>
    public const string MetricsVersion = "v1";

    private const string NoBranch = "ALL_BRANCHES";
    private const string None = "NONE";

    public static string Build(
        Guid organizationId,
        Guid? branchId,
        Guid? categoryId,
        AiSummaryScope scope,
        Guid? subjectUserId,
        string reportType,
        DateOnly periodStart,
        DateOnly periodEnd,
        string metricsHash,
        string promptVersion,
        string modelId)
    {
        var source = string.Join(
            '|',
            organizationId.ToString("N"),
            branchId?.ToString("N") ?? NoBranch,
            categoryId?.ToString("N") ?? None,
            scope.ToString(),
            subjectUserId?.ToString("N") ?? None,
            reportType,
            periodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            periodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            metricsHash,
            MetricsVersion,
            promptVersion,
            modelId);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    /// <summary>
    /// SHA-256 over the canonical JSON of the metrics that were sent (<c>AI-010</c>).
    /// </summary>
    /// <remarks>
    /// This is what makes a stale report impossible to serve: the figures change, the hash changes,
    /// the key changes, and the cached answer is simply not found rather than returned out of date.
    /// </remarks>
    public static string HashMetrics(string canonicalJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
}
