namespace Splitly.Domain.Splitting;

public enum Recurrence { Daily = 0, Weekly = 1, Monthly = 2, Yearly = 3 }

public static class Recurrences
{
    /// <summary>
    /// The date of the n-th occurrence (0-based) counted from <paramref name="start"/>. Always computed from the start,
    /// never by repeatedly adding to the previous date, so month-end clamping doesn't accumulate (Jan 31 -> Feb 28 -> Mar 31).
    /// </summary>
    public static DateOnly OccurrenceDate(DateOnly start, Recurrence recurrence, int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        return recurrence switch
        {
            Recurrence.Daily => start.AddDays(n),
            Recurrence.Weekly => start.AddDays(7 * n),
            Recurrence.Monthly => start.AddMonths(n),
            Recurrence.Yearly => start.AddYears(n),
            _ => throw new DomainValidationException($"Unknown recurrence {recurrence}."),
        };
    }
}
