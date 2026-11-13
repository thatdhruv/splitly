using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Splitly.Api.Auth;
using Splitly.Api.Services;
using Splitly.Data;

namespace Splitly.Api.Hubs;

[Authorize]
public sealed class GroupsHub(AppDbContext db) : Hub
{
    public static string Channel(Guid groupId) => $"group:{groupId}";

    /// <summary>Subscribe to live updates for a group. Same membership rule as the HTTP API.</summary>
    public async Task Join(Guid groupId)
    {
        var userId = Context.User!.GetUserId();
        if (!await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId))
            throw new HubException("You are not a member of this group.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Channel(groupId));
    }

    public Task Leave(Guid groupId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Channel(groupId));
}

public sealed class GroupNotifier(IHubContext<GroupsHub> hub, BalanceService balances)
{
    /// <summary>Pushes the fresh balances to everyone currently viewing the group.</summary>
    public async Task BalancesChangedAsync(Guid groupId, CancellationToken ct = default)
    {
        var dto = await balances.GetBalancesAsync(groupId, ct);
        await hub.Clients.Group(GroupsHub.Channel(groupId)).SendAsync("BalancesChanged", dto, ct);
    }

    /// <summary>Tells viewers that a receipt changed (uploaded, reviewed, claimed or finalized) so they can reload it.</summary>
    public Task ReceiptChangedAsync(Guid groupId, Guid receiptId, CancellationToken ct = default) =>
        hub.Clients.Group(GroupsHub.Channel(groupId)).SendAsync("ReceiptChanged", groupId, receiptId, ct);
}
