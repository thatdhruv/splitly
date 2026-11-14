using Microsoft.Extensions.Configuration;
var builder = DistributedApplication.CreateBuilder(args);

// SQL Server in a container; the database "splitly" is created for us. Data survives restarts of the app host.
var sql = builder.AddSqlServer("sql");
// Keep data between runs during development; tests pass --Splitly:PersistData=false so they leave no container behind.
if (builder.Configuration.GetValue("Splitly:PersistData", true)) sql.WithLifetime(ContainerLifetime.Persistent);
var db = sql.AddDatabase("splitly");

// Azure Blob Storage for receipt photos: Azurite locally, a real storage account when deployed.
var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var receipts = storage.AddBlobs("receipts");

// The API applies EF migrations on start, so everything else waits for it.
var api = builder.AddProject<Projects.Splitly_Api>("api")
    .WithReference(db).WaitFor(db)
    .WithReference(receipts).WaitFor(receipts)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// Receipt parsing and recurring expenses. With an Anthropic key (user secret "Parameters:anthropic-api-key") it uses Claude
// vision; without one it falls back to the deterministic fake parser so local development works out of the box.
var worker = builder.AddProject<Projects.Splitly_Worker>("worker")
    .WithReference(db)
    .WithReference(receipts)
    .WaitFor(api);

if (!string.IsNullOrEmpty(builder.Configuration["Parameters:anthropic-api-key"]))
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

// Passkeys (WebAuthn) are bound to the website the browser sees, which is the web app, not the API.
api.WithEnvironment("Passkeys__RelyingPartyId", builder.Configuration["Passkeys:RelyingPartyId"] ?? "localhost")
   .WithEnvironment("Passkeys__AllowedOrigins__0", web.GetEndpoint("http"));

builder.Build().Run();
