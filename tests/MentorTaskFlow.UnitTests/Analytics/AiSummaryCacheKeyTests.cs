using MentorTaskFlow.Domain.Analytics;

namespace MentorTaskFlow.UnitTests.Analytics;

/// <summary>The cache key of <c>AI-009</c> and the leak it exists to prevent (<c>TEN-076</c>).</summary>
public sealed class AiSummaryCacheKeyTests
{
    private static readonly Guid Organization = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HeadOffice = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Khujand = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Category = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 30);

    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string Key(Guid? branchId, Guid? categoryId = null, string metricsHash = Hash) =>
        AiSummaryCacheKey.Build(
            Organization,
            branchId,
            categoryId ?? Category,
            AiSummaryScope.Team,
            subjectUserId: null,
            "period-summary",
            From,
            To,
            metricsHash,
            "v1.0",
            "claude-sonnet-5");

    [Fact]
    public void The_same_inputs_produce_the_same_key()
    {
        Key(HeadOffice).ShouldBe(Key(HeadOffice));
    }

    /// <summary>
    /// <c>TEN-076</c>, the whole reason the tenant scope is in the key.
    /// </summary>
    /// <remarks>
    /// Two branches, the same category name, the same period. If the keys matched, the second branch's
    /// request would be answered out of the cache with the first branch's report — and it would look
    /// like a cache hit, not like an error.
    /// </remarks>
    [Fact]
    public void Two_branches_never_share_a_key()
    {
        Key(HeadOffice).ShouldNotBe(Key(Khujand));
    }

    [Fact]
    public void The_all_branches_aggregate_has_its_own_key()
    {
        AiSummaryCacheKey
            .Build(Organization, null, null, AiSummaryScope.Organization, null, "period-summary", From, To, Hash, "v1.0", "m")
            .ShouldNotBe(
                AiSummaryCacheKey.Build(Organization, HeadOffice, null, AiSummaryScope.Branch, null, "period-summary", From, To, Hash, "v1.0", "m"));
    }

    /// <summary><c>AI-010</c>: changed metrics must not be served from an old report.</summary>
    [Fact]
    public void Changed_metrics_change_the_key()
    {
        var other = new string('a', 64);

        Key(HeadOffice).ShouldNotBe(Key(HeadOffice, metricsHash: other));
    }

    /// <summary>A new prompt or a new model is a new report, not the old one relabelled.</summary>
    [Fact]
    public void The_prompt_version_and_the_model_are_part_of_the_key()
    {
        var baseline = Key(HeadOffice);

        AiSummaryCacheKey
            .Build(Organization, HeadOffice, Category, AiSummaryScope.Team, null, "period-summary", From, To, Hash, "v2.0", "claude-sonnet-5")
            .ShouldNotBe(baseline);

        AiSummaryCacheKey
            .Build(Organization, HeadOffice, Category, AiSummaryScope.Team, null, "period-summary", From, To, Hash, "v1.0", "other-model")
            .ShouldNotBe(baseline);
    }

    /// <summary>
    /// The separator is load-bearing: without it a category id ending where a literal begins could
    /// reproduce another scope's concatenation.
    /// </summary>
    [Fact]
    public void A_missing_branch_and_a_missing_category_are_distinguished()
    {
        AiSummaryCacheKey
            .Build(Organization, null, Category, AiSummaryScope.Team, null, "period-summary", From, To, Hash, "v1.0", "m")
            .ShouldNotBe(
                AiSummaryCacheKey.Build(Organization, HeadOffice, null, AiSummaryScope.Team, null, "period-summary", From, To, Hash, "v1.0", "m"));
    }

    [Fact]
    public void The_key_fits_the_column()
    {
        Key(HeadOffice).Length.ShouldBe(64);
        Key(HeadOffice).Length.ShouldBeLessThanOrEqualTo(AiSummary.CacheKeyMaxLength);
    }
}
