# Splitly

A Splitwise-style expense splitter on .NET 10: ASP.NET Core minimal API, EF Core + SQL Server, a Blazor web app, a .NET MAUI
mobile app for camera capture, a background worker for receipt parsing and recurring expenses, SignalR live balances, and
.NET Aspire orchestration with OpenTelemetry.

## Features
- **Accounts:** email + password, or **passkeys** (WebAuthn, via ASP.NET Core Identity). Groups with expiring, limited-use invite links.
- **Expenses:** equal, exact, or percentage splits. Multi-currency with the exchange rate locked at the expense date.
  **Recurring** expenses (daily/weekly/monthly/yearly) that catch up on missed occurrences.
- **Receipts:** upload a photo (web or phone camera); a worker extracts line items with Claude vision. Low-confidence lines and
  totals that don't add up are flagged for review. Members claim their items; tax and tip are split proportionally.
- **Live balances** for every member over SignalR, and **settle up** with the minimum number of payments (greedy net-balance).
- **Safety:** money is `decimal`, split on integer minor units with deterministic rounding so shares always total exactly;
  optimistic concurrency (rowversion) on expenses and receipts; every `/groups/{groupId}/...` route sits behind one membership policy.

## Layout
| Project | What it is |
|---|---|
| `Splitly.Domain` | Pure money logic: allocation, split calculators, receipt tax/tip split, FX, recurrence, settle-up, telemetry |
| `Splitly.Data` | EF Core model and migrations |
| `Splitly.Application` | Expense, recurring and FX services shared by the API and the worker |
| `Splitly.Contracts` | DTOs shared by the API, web and mobile app |
| `Splitly.Api` | Minimal API + SignalR hub + passkey endpoints |
| `Splitly.Receipts` | Blob storage, the receipt parser (Claude vision / fake) and processor |
| `Splitly.Worker` | Background service: receipt parsing and recurring expenses |
| `Splitly.Web` | Blazor (interactive server) UI |
| `Splitly.Mobile` | .NET MAUI app (not in `Splitly.slnx`, see below) |
| `Splitly.AppHost`, `Splitly.ServiceDefaults` | Aspire orchestration and shared OpenTelemetry/health defaults |

## Run it locally
Needs the .NET 10 SDK and Docker.
```
dotnet run --project src/Splitly.AppHost
```
This starts SQL Server and Azurite in containers plus the API, worker and web app, with the Aspire dashboard for traces, metrics and logs.
Receipt parsing uses a deterministic **fake** parser unless you give it a key:
```
dotnet user-secrets --project src/Splitly.AppHost set Parameters:anthropic-api-key <your key>
```
Details worth knowing:
- Passkeys need a real host name or `localhost`; the AppHost wires the relying party to the web app automatically.
- Outside Aspire, run the API with `ConnectionStrings__splitly`, and the worker with the same plus `Receipts__Parser=fake|claude`
  (`claude` needs `ANTHROPIC_API_KEY`; the worker refuses to start without it). Without `ConnectionStrings__receipts`, photos go to
  `Receipts__LocalPath`, which the API and worker must share.

## Mobile app
`src/Splitly.Mobile` signs in, lists your groups, scans a receipt with the camera (re-encoded to a JPEG under the 5 MB limit),
uploads it, and lets you claim your items. Reviewing flagged lines, tax/tip and "add expense" are done on the web app.
It is kept out of the main solution because its Android/iOS targets need platform workloads. Build a single platform with:
```
dotnet workload install maui-windows    # or maui-android
dotnet build src/Splitly.Mobile -p:SplitlyMobileTargets=net10.0-windows10.0.19041.0
```
Use an HTTPS server address on real devices (the Android emulator reaches your machine at `10.0.2.2`). Passkey sign-in is web-only for now.

## Tests
`dotnet test` runs everything (Docker required; install the browser once with
`pwsh tests/Splitly.E2E.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`):
- **Domain**: unit tests plus FsCheck property tests (shares total exactly, balances sum to zero, settle-up clears every balance).
- **API**: integration tests against a real SQL Server (Testcontainers): authorization on every route, splits, FX, concurrency,
  invites, receipts, worker races and recovery, recurring schedules, and a property test that group balances sum to zero.
- **AppHost**: boots the full Aspire model (SQL, Azurite, API, worker, web) and sends a receipt through it.
- **E2E**: Playwright drives Chromium through sign-up, live updates between two browsers, receipts, recurring expenses, and passkeys
  (using a virtual authenticator).

CI (`.github/workflows/ci.yml`) runs all of the above on every push. `mobile.yml` builds the mobile app; `deploy.yml` is a manual Azure deploy.

## Deploying
See [docs/DEPLOY.md](docs/DEPLOY.md): Azure Container Apps + Azure SQL + Storage + Application Insights via `azd`.
