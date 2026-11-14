using Splitly.Web.Components;
using Splitly.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(); // OpenTelemetry, service discovery, resilient HttpClients

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The API base URL comes from config (Aspire injects it in local/dev and Container Apps).
var apiBase = builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5288";
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/"));
// One ApiClient per circuit (i.e. per signed-in browser tab), shared by the layout and every page. A typed client would be
// transient: each component would get its own instance with its own session state.
builder.Services.AddScoped(sp => new ApiClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("api"),
    sp.GetRequiredService<Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage.ProtectedSessionStorage>()));

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
