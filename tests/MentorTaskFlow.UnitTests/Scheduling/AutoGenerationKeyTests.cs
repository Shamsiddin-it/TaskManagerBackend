using MentorTaskFlow.Domain.Assignments;

namespace MentorTaskFlow.UnitTests.Scheduling;

/// <summary>The idempotency key of auto-generation (TZ 20.3).</summary>
public sealed class AutoGenerationKeyTests
{
    private static readonly Guid Organization = Guid.Parse("019f0000-0000-7000-8000-000000000001");
    private static readonly Guid HeadOffice = Guid.Parse("019f0000-0000-7000-8000-000000000002");
    private static readonly Guid Khujand = Guid.Parse("019f0000-0000-7000-8000-000000000003");
    private static readonly Guid Category = Guid.Parse("019f0000-0000-7000-8000-000000000004");
    private static readonly Guid Template = Guid.Parse("019f0000-0000-7000-8000-000000000005");
    private static readonly DateOnly Date = new(2026, 9, 1);

    [Fact]
    public void The_key_follows_the_template() =>
        AutoGenerationKey.For(Organization, HeadOffice, Category, Template, Date)
            .ShouldBe($"{Organization:N}:{HeadOffice:N}:{Category:N}:{Template:N}:2026-09-01:Auto");

    /// <summary>
    /// The mentor is deliberately absent (<c>SCH-009</c>): a second run must not create a second task
    /// merely because the balancing picked somebody else.
    /// </summary>
    [Fact]
    public void The_key_does_not_depend_on_the_mentor()
    {
        var first = AutoGenerationKey.For(Organization, HeadOffice, Category, Template, Date);
        var second = AutoGenerationKey.For(Organization, HeadOffice, Category, Template, Date);

        first.ShouldBe(second);
    }

    [Fact]
    public void A_different_day_produces_a_different_key() =>
        AutoGenerationKey.For(Organization, HeadOffice, Category, Template, Date)
            .ShouldNotBe(AutoGenerationKey.For(Organization, HeadOffice, Category, Template, Date.AddDays(1)));

    [Fact]
    public void A_different_branch_produces_a_different_key() =>
        AutoGenerationKey.For(Organization, HeadOffice, Category, Template, Date)
            .ShouldNotBe(AutoGenerationKey.For(Organization, Khujand, Category, Template, Date));

    /// <summary>
    /// The column is 160 characters wide because this template needs 147.
    /// </summary>
    /// <remarks>
    /// <c>SCH-008</c> declares <c>varchar(120)</c>, which the template of <c>SCH-009</c> cannot fit —
    /// the two requirements contradict each other. The template won; this test pins the length so the
    /// column is never narrowed back under it.
    /// </remarks>
    [Fact]
    public void The_key_fits_the_column()
    {
        var key = AutoGenerationKey.For(Organization, HeadOffice, Category, Template, Date);

        key.Length.ShouldBe(147);
        key.Length.ShouldBeLessThanOrEqualTo(160);
    }
}
