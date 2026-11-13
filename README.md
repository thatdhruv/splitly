# Splitly

A Splitwise-style expense splitter built on .NET 10: ASP.NET Core minimal API, EF Core + SQL Server,
Blazor front end, background receipt parsing, SignalR live balances.

## Projects
| Project | What it is |
|---|---|
| `Splitly.Domain` | Pure money logic: exact allocation, split calculators, receipt tax/tip split, FX, settle-up |
| `Splitly.Data` | EF Core model, migrations (rowversion on expenses and receipts) |
| `Splitly.Api` | Minimal API + SignalR hub; one membership policy guards every `/groups/{groupId}` route |
| `Splitly.Receipts` | Blob storage, receipt parser (Claude vision / fake), the processing logic |
| `Splitly.Worker` | Background service that parses uploaded receipts |
| `Splitly.Web` | Blazor (interactive server) UI |

## Money rules
- All money is `decimal`; all splitting runs on integer minor units (`long`) via a largest-remainder allocator.
- Splits always total exactly; ties break deterministically on user id.
- Foreign-currency expenses lock their exchange rate at the expense date and are converted to the group's base currency once.

## Receipts
1. A member uploads a JPEG/PNG/WebP (max 5 MB; the real format is sniffed, not trusted).
2. The worker claims it atomically, reads the image from blob storage and asks the parser for line items.
3. Items the model is unsure about (confidence < 0.8), negative lines, and totals that don't match the printed total are
   flagged; members review them before anything can be split.
4. Members claim their own items (shared items split equally). Tax and tip are split in proportion to each member's subtotal.
5. Finalizing turns the receipt into an exact-split expense.

### Running the worker
```
# real parsing (Claude vision)
ANTHROPIC_API_KEY=... dotnet run --project src/Splitly.Worker
# local development without a key (returns a fixed sample receipt)
Receipts__Parser=fake dotnet run --project src/Splitly.Worker
```
Storage: set `ConnectionStrings__receipts` for Azure Blob Storage/Azurite, otherwise files go to `Receipts__LocalPath`
(the API and worker must point at the same place). The worker refuses to start with `Receipts__Parser=claude` and no key.

## Tests
`dotnet test` runs domain unit + property tests, API integration tests against a real SQL Server (Testcontainers; needs Docker),
and Playwright end-to-end tests (`pwsh tests/Splitly.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium` once).
