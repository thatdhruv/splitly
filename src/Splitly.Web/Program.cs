using Splitly.Web.Components;
using Splitly.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(); // OpenTelemetry, service discovery, resilient HttpClients

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The API base URL comes from config (Aspire injects it in local/dev and Container Apps).
var apiBase = builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5288";
builder.Services.AddHttpClient<ApiClient>(c => c.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();

public partial class Program;
