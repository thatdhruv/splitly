using Splitly.Contracts;
using Splitly.Mobile.Services;

namespace Splitly.Mobile.Pages;

public partial class GroupsPage : ContentPage
{
    private readonly ApiClient _api;

    public GroupsPage(ApiClient api)
    {
        InitializeComponent();
        _api = api;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh.IsRefreshing = true;
    }

    private async void OnRefresh(object? sender, EventArgs e)
    {
        try { List.ItemsSource = await _api.GetGroupsAsync(); }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized) { await SignOutAsync(); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            await DisplayAlertAsync("Couldn't load your groups", ex.Message, "OK");
        }
        finally { Refresh.IsRefreshing = false; }
    }

    private async void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not GroupSummaryDto group) return;
        List.SelectedItem = null;
        await Shell.Current.GoToAsync($"group?id={group.Id}&name={Uri.EscapeDataString(group.Name)}");
    }

    private async void OnSignOut(object? sender, EventArgs e) => await SignOutAsync();

    private async Task SignOutAsync()
    {
        _api.SignOut();
        await Shell.Current.GoToAsync("//login");
    }
}
