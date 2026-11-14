using Splitly.Mobile.Services;

namespace Splitly.Mobile.Pages;

public partial class LoginPage : ContentPage
{
    private readonly ApiClient _api;

    public LoginPage(ApiClient api)
    {
        InitializeComponent();
        _api = api;
        // 10.0.2.2 is how the Android emulator reaches the host machine's localhost.
        ServerEntry.Text = Preferences.Default.Get("server",
            DeviceInfo.Platform == DevicePlatform.Android ? "https://10.0.2.2:7063" : "https://localhost:7063");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_api.IsSignedIn || await _api.RestoreAsync()) await Shell.Current.GoToAsync("groups");
    }

    private async void OnSignIn(object? sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;
        SetBusy(true);
        try
        {
            await _api.SignInAsync(ServerEntry.Text ?? "", EmailEntry.Text ?? "", PasswordEntry.Text ?? "");
            Preferences.Default.Set("server", _api.Server!);
            PasswordEntry.Text = "";
            await Shell.Current.GoToAsync("groups");
        }
        catch (ApiException ex) { Show(ex.Message); }
        catch (HttpRequestException) { Show("Can't reach the server. Check the address and your connection."); }
        catch (TaskCanceledException) { Show("The server took too long to answer."); }
        finally { SetBusy(false); }
    }

    private void Show(string message) { ErrorLabel.Text = message; ErrorLabel.IsVisible = true; }
    private void SetBusy(bool busy) { SignInButton.IsEnabled = !busy; Busy.IsVisible = Busy.IsRunning = busy; }
}
