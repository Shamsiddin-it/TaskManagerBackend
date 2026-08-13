using MentorTaskFlow.Infrastructure.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace MentorTaskFlow.UnitTests.Assignments;

/// <summary>
/// Deadline arithmetic and the two daylight-saving rules (TZ 14.2, 14.3).
/// </summary>
/// <remarks>
/// The DST cases use Europe/Berlin because Asia/Dushanbe has observed no daylight saving since 1991,
/// so it cannot exercise either rule. The rules themselves are zone-independent.
/// </remarks>
public sealed class DeadlineCalculatorTests
{
    private readonly DeadlineCalculator _calculator = new(NullLogger<DeadlineCalculator>.Instance);

    /// <summary>
    /// <c>SCH-020</c>: planned date plus the category's due days, at its default time of day, in its
    /// zone. Version 2.0 stopped at «PlannedDate + DueDays» and left the time undefined.
    /// </summary>
    [Fact]
    public void The_deadline_is_the_planned_date_plus_due_days_at_the_default_time()
    {
        var result = _calculator.CalculateInitialDueAt(
            new DateOnly(2026, 9, 1),
            dueDays: 3,
            new TimeOnly(23, 59),
            "Asia/Dushanbe");

        // Asia/Dushanbe is UTC+5 year-round: 4 September 23:59 local is 18:59 UTC.
        result.ShouldBe(new DateTimeOffset(2026, 9, 4, 18, 59, 0, TimeSpan.Zero));
        result.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void A_local_moment_is_converted_to_utc()
    {
        var result = _calculator.ToUtc(new DateOnly(2026, 6, 15), new TimeOnly(9, 0), "Asia/Dushanbe");

        result.ShouldBe(new DateTimeOffset(2026, 6, 15, 4, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// Clocks jump forward on 29 March 2026 in Europe/Berlin: 02:00 becomes 03:00, so 02:30 never
    /// happens. The first moment after the gap is used — a deadline must not vanish because of a
    /// clock change (14.3).
    /// </summary>
    [Fact]
    public void A_time_that_does_not_exist_moves_to_the_first_moment_after_the_gap()
    {
        var result = _calculator.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(2, 30), "Europe/Berlin");

        // 03:00 local, CEST (UTC+2) → 01:00 UTC.
        result.ShouldBe(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// Clocks go back on 25 October 2026 in Europe/Berlin: 02:30 happens twice. The later instant is
    /// chosen, which gives the mentor more time rather than less (14.3).
    /// </summary>
    [Fact]
    public void An_ambiguous_time_resolves_to_the_later_instant()
    {
        var result = _calculator.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(2, 30), "Europe/Berlin");

        // The two candidates are 00:30 UTC (CEST, UTC+2) and 01:30 UTC (CET, UTC+1); the later one
        // wins.
        result.ShouldBe(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// The default of 23:59 was chosen partly because no supported zone shifts its clocks then, so
    /// the ordinary case never touches either rule (14.3).
    /// </summary>
    [Fact]
    public void The_default_deadline_time_is_unaffected_by_transition_days()
    {
        var springForward = _calculator.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(23, 59), "Europe/Berlin");
        var fallBack = _calculator.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(23, 59), "Europe/Berlin");

        springForward.ShouldBe(new DateTimeOffset(2026, 3, 29, 21, 59, 0, TimeSpan.Zero));
        fallBack.ShouldBe(new DateTimeOffset(2026, 10, 25, 22, 59, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// The zone is validated on every save of Branch and CategorySettings, so reaching the calculator
    /// with an unknown one means the host's tzdata differs from the one that accepted it
    /// (<c>BRN-010</c>).
    /// </summary>
    [Fact]
    public void An_unknown_zone_is_a_validation_failure()
    {
        Should.Throw<Application.Common.Exceptions.ValidationAppException>(
            () => _calculator.ToUtc(new DateOnly(2026, 9, 1), new TimeOnly(12, 0), "Asia/Atlantis"));
    }
}
