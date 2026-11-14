# Deploying Splitly to Azure Container Apps

The .NET Aspire AppHost (`src/Splitly.AppHost`) describes the whole system. Locally it runs SQL Server and Azurite in
containers; when *published* the same model becomes Azure resources:

| Aspire resource | Local (run) | Azure (publish) |
|---|---|---|
| `sql` / `splitly` | SQL Server container | Azure SQL (passwordless, managed identity) |
| `storage` / `receipts` | Azurite | Azure Storage account (private blob container) |
| `api`, `worker`, `web` | processes | Container Apps (one managed identity each, with SQL/Storage roles) |
| telemetry | Aspire dashboard | Application Insights (OpenTelemetry via Azure Monitor) |

`dotnet run --project src/Splitly.AppHost -- --publisher manifest --output-path out/manifest.json` produces the manifest and the
Bicep modules without needing an Azure subscription; this is how the model was checked.

> **Status:** the publish model builds and generates a manifest and Bicep. It has **not** been deployed to a real subscription
> in this repository's history, so expect to iron out first-deploy details (quotas, naming, role propagation delays).

## One-time setup
1. Install the [Azure Developer CLI](https://aka.ms/azd) and sign in: `azd auth login`.
2. From the repo root: `azd up`. Choose an environment name (e.g. `prod`), subscription and region.
   azd will prompt for the secret parameter **`anthropic-api-key`** (the receipt parser's Claude API key) and remember it in the
   azd environment. It never goes in source control.
3. Open the printed `web` URL. The API's URL is also public: it is what the mobile app's *Server* field takes.

## Repeat deployments from GitHub Actions
`.github/workflows/deploy.yml` is manual (Actions tab, or push a `v*` tag). It signs in with OIDC, so no Azure secret is stored in GitHub.

1. Create an Entra app registration (or managed identity) for GitHub and give it *Contributor* + *User Access Administrator*
   on the subscription or resource group (the Aspire modules create role assignments).
2. Add a **federated credential** for this repo (`repo:thatdhruv/splitly:environment:prod`).
3. In the repo's *Settings → Environments → prod*, add the variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`, `AZURE_LOCATION`, and consider requiring a reviewer.
4. The workflow reuses the azd environment created in step 2 above (`azd env list`). If you run it from a clean runner, make the
   environment available there first (e.g. `azd env refresh`), since azd keeps the environment's values outside the repo.

## Things to know
- **Single replica for `api` and `web`.** SignalR connections (live balances) and Blazor Server circuits hold state in memory.
  Scaling out needs Azure SignalR Service (a backplane) and sticky sessions. Don't raise `maxReplicas` before then.
- **Passkeys** are bound to the web app's hostname (`Passkeys__RelyingPartyId`) and origin (`Passkeys__AllowedOrigins__0`); both are
  wired from the `web` endpoint automatically. Changing the web app's domain invalidates existing passkeys.
- **Database migrations** run when the API starts (`Database__MigrateOnStartup`, default true). For multi-instance rollouts,
  turn that off and run migrations as a deployment step instead.
- **Exchange rates** come from the public Frankfurter (ECB) API on first use per currency/date, then are cached in the database.
- **Costs:** Azure SQL, the Container Apps environment and Application Insights bill continuously; `azd down` removes everything.
