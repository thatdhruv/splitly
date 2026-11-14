using Splitly.Contracts;
using Splitly.Mobile.Services;

namespace Splitly.Mobile.Pages;

[QueryProperty(nameof(GroupId), "id")]
[QueryProperty(nameof(GroupName), "name")]
public partial class GroupPage : ContentPage
{
    private readonly ApiClient _api;

    public string GroupId { get; set; } = "";
    public string GroupName { set => Title = value; }

    public GroupPage(ApiClient api)
    {
        InitializeComponent();
        _api = api;
        ScanButton.IsEnabled = ReceiptPhoto.CanCapture; // e.g. desktop without a camera: the picker still works
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh.IsRefreshing = true;
    }

    private Guid GroupGuid => Guid.Parse(GroupId);

    private async void OnRefresh(object? sender, EventArgs e)
    {
        try
        {
            var balances = await _api.GetBalancesAsync(GroupGuid);
            BalancesPanel.BindingContext = balances;
            var receipts = await _api.GetReceiptsAsync(GroupGuid);
            BindableLayout.SetItemsSource(ReceiptsPanel, receipts);
            NoReceipts.IsVisible = receipts.Count == 0;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            await DisplayAlertAsync("Couldn't load this group", ex.Message, "OK");
        }
        finally { Refresh.IsRefreshing = false; }
    }

    private async void OnScan(object? sender, EventArgs e) => await CaptureAndUploadAsync(ReceiptPhoto.CaptureAsync);
    private async void OnPick(object? sender, EventArgs e) => await CaptureAndUploadAsync(ReceiptPhoto.PickAsync);

    private async Task CaptureAndUploadAsync(Func<Task<Stream?>> getPhoto)
    {
        SetBusy(true, "Opening camera…");
        try
        {
            await using var photo = await getPhoto();
            if (photo is null) return; // cancelled

            SetBusy(true, "Uploading…");
            var receipt = await _api.UploadReceiptAsync(GroupGuid, photo, $"receipt-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
            await Shell.Current.GoToAsync($"receipt?group={GroupId}&id={receipt.Id}");
        }
        catch (PermissionException) { await DisplayAlertAsync("Camera access needed", "Allow camera (or photo) access for Splitly in your device settings.", "OK"); }
        catch (FeatureNotSupportedException) { await DisplayAlertAsync("No camera", "This device can't take photos. Choose a photo instead.", "OK"); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            await DisplayAlertAsync("Couldn't upload the receipt", ex.Message, "OK");
        }
        finally { SetBusy(false); }
    }

    private async void OnReceiptTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject { BindingContext: ReceiptSummaryDto receipt })
            await Shell.Current.GoToAsync($"receipt?group={GroupId}&id={receipt.Id}");
    }

    private void SetBusy(bool busy, string? message = null)
    {
        ScanButton.IsEnabled = !busy && ReceiptPhoto.CanCapture;
        PickButton.IsEnabled = !busy;
        Busy.IsVisible = Busy.IsRunning = busy;
        StatusLabel.IsVisible = busy;
        StatusLabel.Text = message;
    }
}
