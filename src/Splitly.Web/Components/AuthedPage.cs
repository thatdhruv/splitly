using Microsoft.AspNetCore.Components;
using Splitly.Web.Services;

namespace Splitly.Web.Components;

/// <summary>
/// Base for pages that need a signed-in user. Session restore needs JS interop, so it runs after first render;
/// until then <see cref="Ready"/> is false and pages render a placeholder. Unauthenticated users go to /login.
/// </summary>
public abstract class AuthedPage : ComponentBase
{
    [Inject] protected ApiClient Api { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected bool Ready { get; private set; }
    protected string? Error { get; set; }

    protected abstract Task OnReadyAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        if (!await Api.RestoreAsync())
        {
            Nav.NavigateTo($"/login?returnUrl={Uri.EscapeDataString(Nav.ToBaseRelativePath(Nav.Uri))}");
            return;
        }
        await Guard(OnReadyAsync);
        Ready = true;
        StateHasChanged();
    }

    /// <summary>Runs an API action, surfacing failures as an on-page message instead of crashing the circuit.</summary>
    protected async Task Guard(Func<Task> action)
    {
        try { Error = null; await action(); }
        catch (ApiException ex) { Error = ex.Message; }
        catch (HttpRequestException) { Error = "Can't reach the server. Try again in a moment."; }
    }
}
