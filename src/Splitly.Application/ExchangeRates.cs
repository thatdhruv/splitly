using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Splitly.Data;
using Splitly.Domain;
using Splitly.Domain.Money;

namespace Splitly.Application;

public interface IExchangeRateProvider
{
    /// <summary>Rate such that 1 unit of <paramref name="from"/> = rate units of <paramref name="to"/> on <paramref name="date"/>.</summary>
    Task<decimal> GetRateAsync(string from, string to, DateOnly date, CancellationToken ct);
}

/// <summary>Looks rates up in the local cache table first, then falls back to the public Frankfurter (ECB) API and caches the result.</summary>
public sealed class CachedExchangeRateProvider(AppDbContext db, HttpClient http) : IExchangeRateProvider
{
    public async Task<decimal> GetRateAsync(string from, string to, DateOnly date, CancellationToken ct)
    {
        from = Currencies.Normalize(from);
        to = Currencies.Normalize(to);
        if (from == to) return 1m;

        var cached = await db.ExchangeRates.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Date == date && r.Currency == from && r.BaseCurrency == to, ct);
        if (cached is not null) return cached.Rate;

        var url = $"v1/{date:yyyy-MM-dd}?base={from}&symbols={to}";
        var response = await http.GetFromJsonAsync<FrankfurterResponse>(url, ct)
                       ?? throw new DomainValidationException($"No exchange rate available for {from}->{to}.");
        if (!response.Rates.TryGetValue(to, out var rate) || rate <= 0)
            throw new DomainValidationException($"No exchange rate available for {from}->{to} on {date}.");

        db.ExchangeRates.Add(new ExchangeRateEntry { Date = date, Currency = from, BaseCurrency = to, Rate = rate });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* concurrent insert of the same rate: harmless */ db.ChangeTracker.Clear(); }
        return rate;
    }

    private sealed record FrankfurterResponse(Dictionary<string, decimal> Rates);
}
