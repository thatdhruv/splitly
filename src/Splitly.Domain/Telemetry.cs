using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Splitly.Domain;

/// <summary>
/// The app's own traces and metrics (OpenTelemetry picks up the "Splitly" source and meter in ServiceDefaults).
/// Tags are deliberately low-cardinality (currency, split type, status) and never carry user ids or amounts.
/// </summary>
public static class Telemetry
{
    public const string Name = "Splitly";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> ExpensesCreated =
        Meter.CreateCounter<long>("splitly.expenses.created", description: "Expenses created, by split type and origin");
    public static readonly Counter<long> ReceiptsProcessed =
        Meter.CreateCounter<long>("splitly.receipts.processed", description: "Receipt parse attempts, by outcome");
    public static readonly Histogram<double> ReceiptParseSeconds =
        Meter.CreateHistogram<double>("splitly.receipts.parse.duration", unit: "s", description: "Time spent parsing one receipt");
    public static readonly Counter<long> RecurringGenerated =
        Meter.CreateCounter<long>("splitly.recurring.generated", description: "Expenses generated from recurring schedules");
}
