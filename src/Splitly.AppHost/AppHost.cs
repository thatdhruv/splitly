using Aspire.Hosting.Azure;
using Microsoft.Extensions.Configuration;

// One app model, two worlds:
//   run mode      (F5 / dotnet run):  SQL Server and Azurite in containers, everything on localhost.
//   publish mode  (azd up / deploy):  Azure Container Apps, Azure SQL, Azure Storage, Application Insights.
var builder = DistributedApplication.CreateBuilder(args);
var publishing = builder.ExecutionContext.IsPublishMode;

if (publishing) builder.AddAzureContainerAppEnvironment("env");

// Database: a container locally (kept between runs; tests pass --Splitly:PersistData=false), Azure SQL when published.
var persist = builder.Configuration.GetValue("Splitly:PersistData", true);
var sql = builder.AddAzureSqlServer("sql").RunAsContainer(c =>
{
    if (persist) c.WithLifetime(ContainerLifetime.Persistent);
});
var db = sql.AddDatabase("splitly");

// Receipt photos: Azurite locally, a real storage account when published.
var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var receipts = storage.AddBlobs("receipts");

// Traces, metrics and logs: the Aspire dashboard locally, Application Insights when published.
var insights = publishing ? builder.AddAzureApplicationInsights("insights") : null;

// The API applies EF migrations on start, so everything else waits for it.
var api = builder.AddProject<Projects.Splitly_Api>("api")
    .WithReference(db).WaitFor(db)
    .WithReference(receipts).WaitFor(receipts)
    .WithExternalHttpEndpoints() // the mobile app talks to the API directly
    .WithHttpHealthCheck("/health");

// Receipt parsing + recurring expenses. Locally it uses Claude vision if you've set the user secret
// "Parameters:anthropic-api-key", otherwise the deterministic fake parser so everything works out of the box.
// A published deployment always uses Claude and asks for the key as a secret parameter.
var worker = builder.AddProject<Projects.Splitly_Worker>("worker")
    .WithReference(db)
    .WithReference(receipts)
    .WaitFor(api);

if (publishing || !string.IsNullOrEmpty(builder.Configuration["Parameters:anthropic-api-key"]))
{
    worker.WithEnvironment("ANTHROPIC_API_KEY", builder.AddParameter("anthropic-api-key", secret: true))
          .WithEnvironment("Receipts__Parser", "claude");
}
else
{
    worker.WithEnvironment("Receipts__Parser", "fake");
}

var web = builder.AddProject<Projects.Splitly_Web>("web")
    .WithEnvironment("Api__BaseUrl", api.GetEndpoint("http"))
    .WithReference(api).WaitFor(api)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// Passkeys (WebAuthn) are bound to the website the browser sees, which is the web app, not the API. The relying-party id
// is the web app's host name; the allowed origin is its public URL.
var webEndpoint = web.GetEndpoint(publishing ? "https" : "http");
api.WithEnvironment("Passkeys__RelyingPartyId", webEndpoint.Property(EndpointProperty.Host))
   .WithEnvironment("Passkeys__AllowedOrigins__0", webEndpoint);

if (insights is not null)
{
    api.WithReference(insights);
    worker.WithReference(insights);
    web.WithReference(insights);
}

// NOTE on scale-out: SignalR (live balances) and Blazor Server circuits keep per-connection state in memory, so run a single
// replica of "api" and "web" as deployed here. Scaling out needs a SignalR backplane (Azure SignalR Service) and sticky sessions.

builder.Build().Run();
