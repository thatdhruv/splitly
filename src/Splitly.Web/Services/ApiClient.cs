using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Splitly.Contracts;

namespace Splitly.Web.Services;

public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Per-circuit API client: holds the signed-in user's bearer token and maps API problems to <see cref="ApiException"/>.</summary>
public sealed class ApiClient(HttpClient http, ProtectedSessionStorage storage)
{
    private const string TokenKey = "splitly.token";

    public ProfileDto? Me { get; private set; }
    public string? Token { get; private set; }
    public Uri BaseAddress => http.BaseAddress!;
    public bool IsSignedIn => Me is not null;

    /// <summary>Call from OnAfterRender (needs JS interop). Returns true if a stored session was still valid.</summary>
    public async Task<bool> RestoreAsync()
    {
        if (IsSignedIn) return true;
        var stored = await storage.GetAsync<string>(TokenKey);
        if (!stored.Success || stored.Value is null) return false;
        return await TryUseTokenAsync(stored.Value);
    }

    public async Task LoginAsync(string email, string password)
    {
        var res = await http.PostAsJsonAsync("auth/login", new { email, password });
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new ApiException(res.StatusCode, "Wrong email or password.");
        await EnsureOk(res);
        var token = (await res.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        if (!await TryUseTokenAsync(token)) throw new ApiException(HttpStatusCode.Unauthorized, "Could not start a session.");
        await storage.SetAsync(TokenKey, token);
    }

    public async Task SignUpAsync(string email, string password, string displayName)
    {
        await EnsureOk(await http.PostAsJsonAsync("signup", new SignUpRequest(email, password, displayName)));
        await LoginAsync(email, password);
    }

    public async Task LogoutAsync()
    {
        Token = null; Me = null;
        http.DefaultRequestHeaders.Authorization = null;
        await storage.DeleteAsync(TokenKey);
    }

    private async Task<bool> TryUseTokenAsync(string token)
    {
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await http.GetAsync("me");
        if (!res.IsSuccessStatusCode)
        {
            http.DefaultRequestHeaders.Authorization = null;
            return false;
        }
        Token = token;
        Me = await res.Content.ReadFromJsonAsync<ProfileDto>();
        return true;
    }

    private sealed record LoginResponse(string AccessToken);

    // ---- groups ----
    public Task<List<GroupSummaryDto>> GetGroupsAsync() => GetAsync<List<GroupSummaryDto>>("groups");
    public Task<GroupSummaryDto> CreateGroupAsync(string name, string currency) => PostAsync<GroupSummaryDto>("groups", new CreateGroupRequest(name, currency));
    public Task<GroupDto> GetGroupAsync(Guid id) => GetAsync<GroupDto>($"groups/{id}");
    public Task<InviteDto> CreateInviteAsync(Guid groupId) => PostAsync<InviteDto>($"groups/{groupId}/invites", new CreateInviteRequest());
    public Task<InvitePreviewDto> PreviewInviteAsync(string token) => GetAsync<InvitePreviewDto>($"invites/{Uri.EscapeDataString(token)}");
    public async Task<Guid> AcceptInviteAsync(string token) =>
        (await PostAsync<AcceptResult>("invites/accept", new AcceptInviteRequest(token))).GroupId;
    private sealed record AcceptResult(Guid GroupId);

    // ---- expenses & balances ----
    public Task<List<ExpenseDto>> GetExpensesAsync(Guid groupId) => GetAsync<List<ExpenseDto>>($"groups/{groupId}/expenses");
    public Task<ExpenseDto> AddExpenseAsync(Guid groupId, ExpenseRequest req) => PostAsync<ExpenseDto>($"groups/{groupId}/expenses", req);
    public async Task DeleteExpenseAsync(Guid groupId, ExpenseDto e) =>
        await EnsureOk(await http.DeleteAsync($"groups/{groupId}/expenses/{e.Id}?rowVersion={Uri.EscapeDataString(e.RowVersion)}"));
    public Task<BalancesDto> GetBalancesAsync(Guid groupId) => GetAsync<BalancesDto>($"groups/{groupId}/balances");
    public Task<SettleUpDto> GetSettleUpAsync(Guid groupId) => GetAsync<SettleUpDto>($"groups/{groupId}/settle-up");
    public async Task RecordPaymentAsync(Guid groupId, TransferDto t) =>
        await EnsureOk(await http.PostAsJsonAsync($"groups/{groupId}/payments", new PaymentRequest(t.From, t.To, t.Amount)));

    // ---- recurring ----
    public Task<List<RecurringDto>> GetRecurringAsync(Guid groupId) => GetAsync<List<RecurringDto>>($"groups/{groupId}/recurring");
    public Task<RecurringDto> CreateRecurringAsync(Guid groupId, RecurringRequest req) => PostAsync<RecurringDto>($"groups/{groupId}/recurring", req);
    public async Task StopRecurringAsync(Guid groupId, Guid id) => await EnsureOk(await http.DeleteAsync($"groups/{groupId}/recurring/{id}"));

    // ---- receipts ----
    public async Task<ReceiptDto> UploadReceiptAsync(Guid groupId, Stream file, string fileName, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var part = new StreamContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(part, "file", fileName);
        var res = await http.PostAsync($"groups/{groupId}/receipts", content);
        await EnsureOk(res);
        return (await res.Content.ReadFromJsonAsync<ReceiptDto>())!;
    }

    public Task<List<ReceiptSummaryDto>> GetReceiptsAsync(Guid groupId) => GetAsync<List<ReceiptSummaryDto>>($"groups/{groupId}/receipts");
    public Task<ReceiptDto> GetReceiptAsync(Guid groupId, Guid id) => GetAsync<ReceiptDto>($"groups/{groupId}/receipts/{id}");
    public Task<ReceiptDto> EditItemAsync(Guid groupId, Guid id, Guid itemId, ItemRequest req) => SendAsync<ReceiptDto>(HttpMethod.Put, $"groups/{groupId}/receipts/{id}/items/{itemId}", req);
    public Task<ReceiptDto> AddItemAsync(Guid groupId, Guid id, ItemRequest req) => PostAsync<ReceiptDto>($"groups/{groupId}/receipts/{id}/items", req);
    public Task<ReceiptDto> DeleteItemAsync(Guid groupId, Guid id, Guid itemId) => SendAsync<ReceiptDto>(HttpMethod.Delete, $"groups/{groupId}/receipts/{id}/items/{itemId}", null);
    public Task<ReceiptDto> SetTotalsAsync(Guid groupId, Guid id, ReceiptTotalsRequest req) => SendAsync<ReceiptDto>(HttpMethod.Put, $"groups/{groupId}/receipts/{id}", req);
    public Task<ReceiptDto> AcceptReceiptAsync(Guid groupId, Guid id) => SendAsync<ReceiptDto>(HttpMethod.Post, $"groups/{groupId}/receipts/{id}/accept", null);
    public Task<ReceiptDto> RetryReceiptAsync(Guid groupId, Guid id) => SendAsync<ReceiptDto>(HttpMethod.Post, $"groups/{groupId}/receipts/{id}/retry", null);
    public Task<ReceiptDto> ClaimItemAsync(Guid groupId, Guid id, Guid itemId, bool claimed) => SendAsync<ReceiptDto>(HttpMethod.Put, $"groups/{groupId}/receipts/{id}/items/{itemId}/claim", new ClaimRequest(claimed));
    public Task<ExpenseDto> FinalizeReceiptAsync(Guid groupId, Guid id, FinalizeReceiptRequest req) => PostAsync<ExpenseDto>($"groups/{groupId}/receipts/{id}/finalize", req);

    /// <summary>The image endpoint needs the bearer token, so the page can't use a plain img src; it embeds a data URL instead.</summary>
    public async Task<string> GetReceiptImageDataUrlAsync(Guid groupId, Guid id)
    {
        var res = await http.GetAsync($"groups/{groupId}/receipts/{id}/image");
        await EnsureOk(res);
        var type = res.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return $"data:{type};base64,{Convert.ToBase64String(await res.Content.ReadAsByteArrayAsync())}";
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        var res = await http.SendAsync(req);
        await EnsureOk(res);
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var res = await http.GetAsync(url);
        await EnsureOk(res);
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(string url, object body)
    {
        var res = await http.PostAsJsonAsync(url, body);
        await EnsureOk(res);
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task EnsureOk(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        string message = res.StatusCode switch
        {
            HttpStatusCode.Forbidden => "You don't have access to that group.",
            HttpStatusCode.Conflict => "Someone else changed this at the same time. Reload and try again.",
            _ => "Something went wrong.",
        };
        try
        {
            var problem = await res.Content.ReadFromJsonAsync<Problem>();
            if (!string.IsNullOrEmpty(problem?.Detail)) message = problem.Detail;
            else if (problem?.Errors is { Count: > 0 }) message = string.Join(" ", problem.Errors.Values.SelectMany(v => v));
        }
        catch { /* non-JSON error body: keep the default message */ }
        throw new ApiException(res.StatusCode, message);
    }

    private sealed record Problem(string? Detail, Dictionary<string, string[]>? Errors);
}
