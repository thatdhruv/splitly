using Microsoft.EntityFrameworkCore;
using Splitly.Application;
using Splitly.Data;
using Splitly.Receipts;
using Splitly.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("splitly"), sql => sql.EnableRetryOnFailure()));
builder.Services.AddReceiptProcessing(builder.Configuration); // fails fast if the parser isn't configured

// Recurring expenses create real expenses, so the worker needs the same expense service (and FX lookup) as the API.
builder.Services.AddHttpClient<IExchangeRateProvider, CachedExchangeRateProvider>(c => c.BaseAddress = new Uri("https://api.frankfurter.dev/"));
builder.Services.AddScoped<ExpenseService>();
builder.Services.AddScoped<RecurringProcessor>();

builder.Services.AddHostedService<ReceiptWorker>();
builder.Services.AddHostedService<RecurringWorker>();

builder.Build().Run();
