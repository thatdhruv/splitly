using Splitly.Contracts;
using Splitly.Mobile.Services;

namespace Splitly.Mobile.Pages;

[QueryProperty(nameof(GroupId), "group")]
[QueryProperty(nameof(ReceiptId), "id")]
public partial class ReceiptPage : ContentPage
{
    private readonly ApiClient _api;
    private ReceiptDto? _receipt;
    private bool _loading;
    private CancellationTokenSource? _poll;

    public string GroupId { get; set; } = "";
    public string ReceiptId { get; set; } = "";

    public ReceiptPage(ApiClient api)
    {
        InitializeComponent();
        _api = api;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _poll = new CancellationTokenSource();
        await LoadAsync();
        // The background worker parses the photo; poll until it has finished.
        _ = PollAsync(_poll.Token);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _poll?.Cancel();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (_receipt is { Status: "Uploaded" or "Processing" } && await timer.WaitForNextTickAsync(ct))
                await MainThread.InvokeOnMainThreadAsync(LoadAsync);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadAsync()
    {
        try
        {
            _receipt = await _api.GetReceiptAsync(Guid.Parse(GroupId), Guid.Parse(ReceiptId));
            Render();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            StatusLabel.Text = ex.Message;
        }
    }

    private void Render()
    {
        var r = _receipt!;
        Title = r.Merchant ?? "Receipt";
        StatusLabel.Text = r.Status switch
        {
            "Uploaded" or "Processing" => "⏳ Reading your receipt…",
            "NeedsReview" => "⚠️ Some lines need a check before it can be split.",
            "Ready" => "✅ Tick the items that are yours.",
            "Failed" => $"❌ {r.FailureReason}",
            "Finalized" => "🧾 Added to the group's expenses.",
            _ => r.Status,
        };
        NoteLabel.Text = r.Note;
        NoteLabel.IsVisible = r.Note is not null;

        _loading = true; // setting Switch.IsToggled below must not send claims to the server
        BindableLayout.SetItemsSource(ItemsPanel, r.Items);
        ApplyClaims(r);
        _loading = false;

        var canClaim = r.Status is "NeedsReview" or "Ready";
        foreach (var sw in ItemsPanel.Children.SelectMany(Switches)) sw.IsEnabled = canClaim;
        HintLabel.Text = r.Status == "Ready" || r.Status == "NeedsReview"
            ? "Review flagged lines, add tax and tip, and add the expense from the website."
            : "";
    }

    private void ApplyClaims(ReceiptDto r)
    {
        var me = _api.Me!.Id;
        foreach (var row in ItemsPanel.Children.OfType<Grid>())
            if (row.BindingContext is ReceiptItemDto item)
                foreach (var sw in Switches(row)) sw.IsToggled = item.ClaimedBy.Contains(me);
    }

    private static IEnumerable<Switch> Switches(IView view) => view switch
    {
        Switch s => [s],
        Layout layout => layout.Children.SelectMany(Switches),
        _ => [],
    };

    private async void OnClaimToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading || sender is not Switch { BindingContext: ReceiptItemDto item } sw) return;
        try
        {
            _receipt = await _api.ClaimAsync(Guid.Parse(GroupId), Guid.Parse(ReceiptId), item.Id, e.Value);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            _loading = true; sw.IsToggled = !e.Value; _loading = false; // put the switch back
            await DisplayAlertAsync("Couldn't update your claim", ex.Message, "OK");
        }
    }
}
