using Splitly.Domain.Money;

namespace Splitly.Domain.Splitting;

public enum SplitType { Equal = 0, Exact = 1, Percentage = 2 }

/// <param name="Value">Ignored for Equal; the owed amount for Exact; the percentage (0-100) for Percentage.</param>
public sealed record SplitInput(Guid UserId, decimal? Value = null);

public readonly record struct Share(Guid UserId, decimal Amount);

public static class SplitCalculator
{
    /// <summary>Splits <paramref name="total"/> among participants. Output is ordered by user id and sums exactly to total.</summary>
    public static IReadOnlyList<Share> Calculate(SplitType type, decimal total, string currency, IReadOnlyList<SplitInput> inputs)
    {
        if (total <= 0) throw new DomainValidationException("Expense amount must be positive.");
        if (inputs.Count == 0) throw new DomainValidationException("An expense needs at least one participant.");
        if (inputs.Select(i => i.UserId).Distinct().Count() != inputs.Count)
            throw new DomainValidationException("A participant appears more than once in the split.");

        var ordered = inputs.OrderBy(i => i.UserId).ToArray();
        var totalMinor = Currencies.ToMinor(total, currency);

        long[] minor = type switch
        {
            SplitType.Equal => Allocator.AllocateEvenly(totalMinor, ordered.Length),
            SplitType.Exact => Exact(totalMinor, currency, ordered),
            SplitType.Percentage => Percentage(totalMinor, ordered),
            _ => throw new DomainValidationException($"Unknown split type {type}."),
        };

        return ordered.Select((o, i) => new Share(o.UserId, Currencies.FromMinor(minor[i], currency))).ToArray();
    }

    private static long[] Exact(long totalMinor, string currency, SplitInput[] ordered)
    {
        var minor = ordered.Select(o => Currencies.ToMinor(RequireValue(o), currency)).ToArray();
        if (minor.Any(m => m < 0)) throw new DomainValidationException("Exact amounts cannot be negative.");
        if (minor.Sum() != totalMinor)
            throw new DomainValidationException(
                $"Exact amounts add up to {Currencies.FromMinor(minor.Sum(), currency)} but the expense is {Currencies.FromMinor(totalMinor, currency)}.");
        return minor;
    }

    private static long[] Percentage(long totalMinor, SplitInput[] ordered)
    {
        var pct = ordered.Select(RequireValue).ToArray();
        if (pct.Any(p => p < 0)) throw new DomainValidationException("Percentages cannot be negative.");
        if (pct.Sum() != 100m) throw new DomainValidationException($"Percentages add up to {pct.Sum()}%, not 100%.");
        return Allocator.Allocate(totalMinor, pct);
    }

    private static decimal RequireValue(SplitInput i) =>
        i.Value ?? throw new DomainValidationException($"Participant {i.UserId} is missing a value for this split type.");
}
