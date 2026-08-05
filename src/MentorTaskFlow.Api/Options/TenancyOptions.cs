namespace MentorTaskFlow.Api.Options;

/// <summary>Tenancy configuration. Source of truth: Приложение L.7.</summary>
public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    /// <summary><c>TENANCY__BRANCH_HEADER_NAME</c>, default <c>X-MTF-Branch-Id</c> (<c>TEN-032</c>).</summary>
    public string BranchHeaderName { get; init; } = "X-MTF-Branch-Id";

    /// <summary><c>TENANCY__ALLOW_ALL_BRANCHES_READ</c> (<c>TEN-034</c>).</summary>
    public bool AllowAllBranchesRead { get; init; } = true;

    /// <summary>Response header carrying the branch actually applied (<c>API-027</c>).</summary>
    public const string EffectiveBranchHeaderName = "X-MTF-Effective-Branch-Id";

    /// <summary>Value of <see cref="EffectiveBranchHeaderName"/> in the all-branches read context.</summary>
    public const string AllBranchesHeaderValue = "all";
}
