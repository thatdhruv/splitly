using System.Numerics;

namespace Splitly.Domain.Money;

/// <summary>
/// Deterministic largest-remainder allocation. The result always sums to exactly <c>total</c>.
/// Remainders are compared with exact integer arithmetic (weights are scaled to BigInteger), and ties
/// go to the lowest index, so identical input always gives identical output. Callers pass a stable ordering.
/// </summary>
public static class Allocator
{
    public static long[] Allocate(long total, IReadOnlyList<decimal> weights)
    {
        if (weights.Count == 0) throw new DomainValidationException("Cannot allocate across zero participants.");
        if (weights.Any(w => w < 0)) throw new DomainValidationException("Allocation weights cannot be negative.");

        var sign = total < 0 ? -1 : 1;
        var abs = new BigInteger(total) * sign;

        var scale = weights.Max(Scale);
        var scaled = weights.Select(w => ToScaled(w, scale)).ToArray();
        var sum = scaled.Aggregate(BigInteger.Zero, (a, b) => a + b);

        if (sum.IsZero)
        {
            if (total == 0) return new long[weights.Count];
            throw new DomainValidationException("Cannot allocate a non-zero amount when all weights are zero.");
        }

        var result = new long[weights.Count];
        var remainders = new BigInteger[weights.Count];
        BigInteger assigned = 0;
        for (var i = 0; i < scaled.Length; i++)
        {
            var floor = BigInteger.DivRem(abs * scaled[i], sum, out remainders[i]);
            result[i] = (long)floor;
            assigned += floor;
        }

        var leftover = (int)(abs - assigned); // 0 <= leftover < n
        foreach (var i in Enumerable.Range(0, weights.Count)
                     .OrderByDescending(i => remainders[i]).ThenBy(i => i).Take(leftover))
            result[i]++;

        if (sign < 0)
            for (var i = 0; i < result.Length; i++) result[i] = -result[i];
        return result;
    }

    public static long[] AllocateEvenly(long total, int count) =>
        Allocate(total, Enumerable.Repeat(1m, count).ToArray());

    private static int Scale(decimal d) => (decimal.GetBits(d)[3] >> 16) & 0xFF;

    private static BigInteger ToScaled(decimal d, int targetScale)
    {
        var bits = decimal.GetBits(d);
        var mantissa = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        return mantissa * BigInteger.Pow(10, targetScale - Scale(d));
    }
}
