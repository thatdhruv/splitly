using Microsoft.EntityFrameworkCore;
using Splitly.Data;
using Splitly.Receipts;
using Splitly.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("splitly"), sql => sql.EnableRetryOnFailure()));
builder.Services.AddReceiptProcessing(builder.Configuration); // fails fast if the parser isn't configured
builder.Services.AddHostedService<ReceiptWorker>();

builder.Build().Run();
