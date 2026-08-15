using MentorTaskFlow.Contracts.Analytics;

namespace MentorTaskFlow.Infrastructure.Analytics;

/// <summary>One breakdown line as the model is allowed to see it.</summary>
/// <remarks>
/// <see cref="Label"/> is an anonymised designation — <c>Ментор 1</c>, <c>Филиал 2</c> — never a name
/// and never an identifier (22.3, <c>TEN-078</c>). The labels are local to one report and are not
/// reused across branches, so two reports cannot be joined on them.
/// </remarks>
public sealed record AiPromptGroup(string Label, MetricsDto Metrics);

/// <summary>
/// Everything that may be sent to the provider, and nothing else (<c>AI-006</c>).
/// </summary>
/// <remarks>
/// <para>
/// This type <b>is</b> the field allowlist. It is the reason the minimisation rule is enforceable
/// rather than aspirational: a domain entity cannot be passed to the provider because the provider
/// takes this type, and adding a forbidden field would mean adding a property here, in a file whose
/// only purpose is to say what is permitted.
/// </para>
/// <para>
/// Note what is absent by construction and must stay absent: organization and branch UUIDs,
/// <c>Organization.Slug</c>, <c>Branch.Code</c>, <c>Branch.Address</c> (<c>TEN-079</c>), and every
/// item of <c>AI-005</c> — names, emails, chat ids, tokens, file contents, presigned URLs.
/// </para>
/// </remarks>
public sealed record AiPromptInput
{
    public required string Scope { get; init; }

    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    /// <summary>The zone that bounded the period (<c>TEN-074</c>), so the model can state it.</summary>
    public required string TimeZoneId { get; init; }

    public required bool IsPartialPeriod { get; init; }

    public required MetricsDto Current { get; init; }

    /// <summary>The same period length immediately before, for the dynamics of 22.1.</summary>
    public MetricsDto? Previous { get; init; }

    public IReadOnlyList<AiPromptGroup> Groups { get; init; } = [];

    /// <summary>Topic names of the period's assignments — permitted by 22.3.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>
    /// Review comments, already truncated and stripped.
    /// </summary>
    /// <remarks>
    /// Empty for the organization aggregate: <c>TEN-078</c> keeps one branch's review text out of a
    /// report an Organization Admin reads about every branch at once.
    /// </remarks>
    public IReadOnlyList<string> Comments { get; init; } = [];
}
