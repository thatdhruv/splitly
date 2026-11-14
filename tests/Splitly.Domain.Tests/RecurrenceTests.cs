using Splitly.Domain.Splitting;

namespace Splitly.Domain.Tests;

public class RecurrenceTests
{
    [Fact]
    public void Monthly_on_the_31st_clamps_in_short_months_but_never_drifts()
    {
        var start = new DateOnly(2026, 1, 31);
        Assert.Equal(new DateOnly(2026, 2, 28), Recurrences.OccurrenceDate(start, Recurrence.Monthly, 1));
        Assert.Equal(new DateOnly(2026, 3, 31), Recurrences.OccurrenceDate(start, Recurrence.Monthly, 2));
        Assert.Equal(new DateOnly(2026, 4, 30), Recurrences.OccurrenceDate(start, Recurrence.Monthly, 3));
        Assert.Equal(new DateOnly(2026, 5, 31), Recurrences.OccurrenceDate(start, Recurrence.Monthly, 4));
    }

    [Fact]
    public void Yearly_on_leap_day_falls_back_to_feb_28_then_returns_in_leap_years()
    {
        var start = new DateOnly(2028, 2, 29);
        Assert.Equal(new DateOnly(2029, 2, 28), Recurrences.OccurrenceDate(start, Recurrence.Yearly, 1));
        Assert.Equal(new DateOnly(2032, 2, 29), Recurrences.OccurrenceDate(start, Recurrence.Yearly, 4));
    }

    [Theory]
    [InlineData(Recurrence.Daily, 10, "2026-03-11")]
    [InlineData(Recurrence.Weekly, 3, "2026-03-22")]
    public void Daily_and_weekly_step_by_fixed_days(Recurrence r, int n, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), Recurrences.OccurrenceDate(new DateOnly(2026, 3, 1), r, n));

    [Fact]
    public void The_first_occurrence_is_the_start_date() =>
        Assert.Equal(new DateOnly(2026, 6, 15), Recurrences.OccurrenceDate(new DateOnly(2026, 6, 15), Recurrence.Monthly, 0));

    [Fact]
    public void Dates_are_strictly_increasing_for_any_schedule()
    {
        foreach (var r in Enum.GetValues<Recurrence>())
        foreach (var start in new[] { new DateOnly(2026, 1, 31), new DateOnly(2028, 2, 29), new DateOnly(2026, 12, 31) })
        {
            var prev = Recurrences.OccurrenceDate(start, r, 0);
            for (var n = 1; n < 60; n++)
            {
                var next = Recurrences.OccurrenceDate(start, r, n);
                Assert.True(next > prev, $"{r} from {start}: occurrence {n} ({next}) is not after {prev}");
                prev = next;
            }
        }
    }
}
