# Splitly

A Splitwise-style expense splitter built on .NET 10: ASP.NET Core minimal API, EF Core + SQL Server,
Blazor front end, background receipt parsing, SignalR live balances, orchestrated with .NET Aspire.

## Money rules
- All money is `decimal`; all splitting runs on integer minor units (`long`) via a largest-remainder allocator.
- Splits always total exactly; ties break deterministically on user id.
- Foreign-currency expenses lock their exchange rate at the expense date and are converted to the group's base currency once.

See `src/Splitly.Domain` for the pure logic and `tests/Splitly.Domain.Tests` for the unit and property tests.
