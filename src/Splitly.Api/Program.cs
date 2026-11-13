using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Splitly.Api.Auth;
using Splitly.Api.Endpoints;
using Splitly.Api.Hubs;
using Splitly.Api.Services;
using Splitly.Data;
using Splitly.Domain;
using Splitly.Receipts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("splitly"), sql => sql.EnableRetryOnFailure()));

builder.Services.AddIdentityApiEndpoints<AppUser>(o =>
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<AppDbContext>();
builder.Services.AddSingleton<IEmailSender<AppUser>, NoOpEmailSender>();

// SignalR browsers can't set headers on WebSocket upgrades, so accept the bearer token from the query string for hub URLs only.
builder.Services.Configure<Microsoft.AspNetCore.Authentication.BearerToken.BearerTokenOptions>(
    IdentityConstants.BearerScheme, o => o.Events.OnMessageReceived = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var t))
            ctx.Token = t;
        return Task.CompletedTask;
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.GroupMember, p => p.RequireAuthenticatedUser().AddRequirements(new GroupMemberRequirement()));
builder.Services.AddScoped<IAuthorizationHandler, GroupMemberHandler>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<IExchangeRateProvider, CachedExchangeRateProvider>(c => c.BaseAddress = new Uri("https://api.frankfurter.dev/"));
builder.Services.AddReceiptStorage(builder.Configuration);
builder.Services.AddScoped<ExpenseService>();
builder.Services.AddScoped<ReceiptService>();
builder.Services.AddScoped<BalanceService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<GroupNotifier>();
builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = ex switch
    {
        DomainValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
        ConcurrencyConflictException => (StatusCodes.Status409Conflict, "Concurrent modification"),
        NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
    };
    ctx.Response.StatusCode = status;
    var detail = status == 500 ? null : ex!.Message;
    await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title, Detail = detail });
}));

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();

app.MapGroup("/auth").MapIdentityApi<AppUser>();
app.MapSplitlyApi();
app.MapHub<GroupsHub>("/hubs/groups");
app.MapGet("/health", () => Results.Ok("healthy")).AllowAnonymous();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.Run();

/// <summary>Identity requires an email sender; real delivery is out of scope until email confirmation is turned on.</summary>
internal sealed class NoOpEmailSender : IEmailSender<AppUser>
{
    public Task SendConfirmationLinkAsync(AppUser user, string email, string confirmationLink) => Task.CompletedTask;
    public Task SendPasswordResetLinkAsync(AppUser user, string email, string resetLink) => Task.CompletedTask;
    public Task SendPasswordResetCodeAsync(AppUser user, string email, string resetCode) => Task.CompletedTask;
}

public partial class Program;
