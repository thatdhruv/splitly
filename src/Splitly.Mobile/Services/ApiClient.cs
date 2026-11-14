using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Splitly.Contracts;

namespace Splitly.Mobile.Services;

public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The phone's view of the Splitly API: sign in, list groups, upload a receipt photo and claim items.
/// The bearer token and server address live in the platform's secure storage (Keychain / Android Keystore).
/// </summary>
public sealed class ApiClient
{
    private const string TokenKey = "splitly.token";
    private const string ServerKey = "splitly.server";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public ProfileDto? Me { get; private set; }
    public string? Server { get; private set; }
    public bool IsSignedIn => Me is not null;

    /// <summary>Restores a saved session. Returns false if there isn't one or it has expired.</summary>
    public async Task<bool> RestoreAsync()
    {
        try
        {
            var server = await SecureStorage.Default.GetAsync(ServerKey);
            var token = await SecureStorage.Default.GetAsync(TokenKey);
            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(token)) return false;
            return await TryUseAsync(server, token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    public async Task SignInAsync(string server, string email, string password)
    {
        server = NormalizeServer(server);
        using var res = await _http.PostAsJsonAsync($"{server}/auth/login", new { email, password });
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new ApiException(res.StatusCode, "Wrong email or password.");
        await EnsureOk(res);
        var token = (await res.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        if (!await TryUseAsync(server, token)) throw new ApiException(HttpStatusCode.Unauthorized, "Could not start a session.");
        await SecureStorage.Default.SetAsync(ServerKey, server);
        await SecureStorage.Default.SetAsync(TokenKey, token);
    }

    public void SignOut()
    {
        Me = null;
        _http.DefaultRequestHeaders.Authorization = null;
        SecureStorage.Default.Remove(TokenKey);
    }

    private async Task<bool> TryUseAsync(string server, string token)
    {
        Server = server;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await _http.GetAsync($"{server}/me");
        if (!res.IsSuccessStatusCode) { _http.DefaultRequestHeaders.Authorization = null; return false; }
        Me = await res.Content.ReadFromJsonAsync<ProfileDto>();
        return true;
    }

    private static string NormalizeServer(string server)
    {
        server = server.Trim().TrimEnd('/');
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ApiException(HttpStatusCode.BadRequest, "Enter the server address, e.g. https://splitly.example.com");
        return server;
    }

    public Task<List<GroupSummaryDto>> GetGroupsAsync() => GetAsync<List<GroupSummaryDto>>("/groups");
    public Task<GroupDto> GetGroupAsync(Guid id) => GetAsync<GroupDto>($"/groups/{id}");
    public Task<BalancesDto> GetBalancesAsync(Guid groupId) => GetAsync<BalancesDto>($"/groups/{groupId}/balances");
    public Task<List<ReceiptSummaryDto>> GetReceiptsAsync(Guid groupId) => GetAsync<List<ReceiptSummaryDto>>($"/groups/{groupId}/receipts");
    public Task<ReceiptDto> GetReceiptAsync(Guid groupId, Guid id) => GetAsync<ReceiptDto>($"/groups/{groupId}/receipts/{id}");

    public async Task<ReceiptDto> UploadReceiptAsync(Guid groupId, Stream jpeg, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var part = new StreamContent(jpeg);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(part, "file", fileName);
        using var res = await _http.PostAsync($"{Server}/groups/{groupId}/receipts", content);
        await EnsureOk(res);
        return (await res.Content.ReadFromJsonAsync<ReceiptDto>())!;
    }

    public async Task<ReceiptDto> ClaimAsync(Guid groupId, Guid receiptId, Guid itemId, bool claimed)
    {
        using var res = await _http.PutAsJsonAsync($"{Server}/groups/{groupId}/receipts/{receiptId}/items/{itemId}/claim", new ClaimRequest(claimed));
        await EnsureOk(res);
        return (await res.Content.ReadFromJsonAsync<ReceiptDto>())!;
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using var res = await _http.GetAsync(Server + path);
        await EnsureOk(res);
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task EnsureOk(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        var message = res.StatusCode switch
        {
            HttpStatusCode.Forbidden => "You don't have access to that group.",
            HttpStatusCode.Unauthorized => "Your session has expired. Please sign in again.",
            _ => "Something went wrong.",
        };
        try
        {
            var problem = await res.Content.ReadFromJsonAsync<Problem>();
            if (!string.IsNullOrEmpty(problem?.Detail)) message = problem.Detail;
        }
        catch { /* non-JSON body: keep the default message */ }
        throw new ApiException(res.StatusCode, message);
    }

    private sealed record LoginResponse(string AccessToken);
    private sealed record Problem(string? Detail);
}
