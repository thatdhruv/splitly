using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Splitly.Api.Auth;
using Splitly.Api.Hubs;
using Splitly.Api.Services;
using Splitly.Contracts;
using Splitly.Data;
using Splitly.Domain;
using Splitly.Domain.Money;

namespace Splitly.Api.Endpoints;

public static class GroupEndpoints
{
    public static void MapSplitlyApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/signup", SignUp).AllowAnonymous();
        app.MapGet("/me", async (HttpContext http, AppDbContext db) =>
        {
            var id = http.User.GetUserId();
            var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == id);
            return new ProfileDto(u.Id, u.Email ?? "", u.DisplayName);
        }).RequireAuthorization();

        // Invites addressed by token (the caller is not yet a member).
        app.MapGet("/invites/{token}", async (string token, InviteService invites, CancellationToken ct) =>
            await invites.PreviewAsync(token, ct)).AllowAnonymous();
        app.MapPost("/invites/accept", async (AcceptInviteRequest req, HttpContext http, InviteService invites, GroupNotifier notifier, CancellationToken ct) =>
        {
            var groupId = await invites.AcceptAsync(req.Token, http.User.GetUserId(), ct);
            await notifier.BalancesChangedAsync(groupId, ct);
            return Results.Ok(new { groupId });
        }).RequireAuthorization();

        var groups = app.MapGroup("/groups").RequireAuthorization();
        groups.MapPost("/", CreateGroup);
        groups.MapGet("/", ListGroups);

        // Everything below requires membership of {groupId}, enforced by one policy.
        var g = groups.MapGroup("/{groupId:guid}").RequireAuthorization(Policies.GroupMember);
        g.MapGet("/", GetGroup);
        g.MapPost("/invites", async (Guid groupId, CreateInviteRequest req, HttpContext http, InviteService invites, CancellationToken ct) =>
            Results.Created($"/groups/{groupId}", await invites.CreateAsync(groupId, http.User.GetUserId(), req, ct)));

        g.MapGet("/balances", async (Guid groupId, BalanceService b, CancellationToken ct) => await b.GetBalancesAsync(groupId, ct));
        g.MapGet("/settle-up", async (Guid groupId, BalanceService b, CancellationToken ct) => await b.GetSettleUpAsync(groupId, ct));
        g.MapPost("/payments", RecordPayment);
        g.MapGet("/payments", ListPayments);

        MapExpenses(g);
        g.MapReceipts();
    }

    private static async Task<IResult> SignUp(SignUpRequest req, UserManager<AppUser> users)
    {
        if (string.IsNullOrWhiteSpace(req.DisplayName)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["displayName"] = ["Required."] });
        var user = new AppUser { UserName = req.Email, Email = req.Email, DisplayName = req.DisplayName.Trim() };
        var result = await users.CreateAsync(user, req.Password);
        return result.Succeeded
            ? Results.Created("/me", new ProfileDto(user.Id, req.Email, user.DisplayName))
            : Results.ValidationProblem(result.Errors.GroupBy(e => e.Code).ToDictionary(e => e.Key, e => e.Select(x => x.Description).ToArray()));
    }

    private static async Task<IResult> CreateGroup(CreateGroupRequest req, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new DomainValidationException("Group name is required.");
        var group = new Group { Name = req.Name.Trim(), BaseCurrency = Currencies.Normalize(req.BaseCurrency) };
        group.Members.Add(new GroupMember { UserId = http.User.GetUserId(), Role = MemberRole.Owner });
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/groups/{group.Id}", new GroupSummaryDto(group.Id, group.Name, group.BaseCurrency, 1));
    }

    private static async Task<IResult> ListGroups(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var userId = http.User.GetUserId();
        var rows = await db.GroupMembers.Where(m => m.UserId == userId)
            .Select(m => new GroupSummaryDto(m.Group!.Id, m.Group.Name, m.Group.BaseCurrency, m.Group.Members.Count))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<GroupDto> GetGroup(Guid groupId, AppDbContext db, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().Include(x => x.Members).ThenInclude(m => m.User).FirstAsync(x => x.Id == groupId, ct);
        return new GroupDto(group.Id, group.Name, group.BaseCurrency,
            group.Members.OrderBy(m => m.JoinedAt).Select(m => new MemberDto(m.UserId, m.User!.DisplayName, m.Role.ToString())).ToList());
    }

    private static async Task<IResult> RecordPayment(Guid groupId, PaymentRequest req, AppDbContext db, GroupNotifier notifier, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().FirstAsync(x => x.Id == groupId, ct);
        if (req.From == req.To) throw new DomainValidationException("A payment needs two different people.");
        if (req.Amount <= 0) throw new DomainValidationException("Payment amount must be positive.");
        Currencies.ToMinor(req.Amount, group.BaseCurrency); // rejects over-precise amounts

        var members = await db.GroupMembers.Where(m => m.GroupId == groupId && (m.UserId == req.From || m.UserId == req.To)).CountAsync(ct);
        if (members != 2) throw new DomainValidationException("Both people must be members of the group.");

        var payment = new Payment { GroupId = groupId, FromUserId = req.From, ToUserId = req.To, Amount = req.Amount };
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        await notifier.BalancesChangedAsync(groupId, ct);
        return Results.Created($"/groups/{groupId}/payments", new PaymentDto(payment.Id, payment.FromUserId, payment.ToUserId, payment.Amount, payment.CreatedAt));
    }

    private static async Task<IResult> ListPayments(Guid groupId, AppDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Payments.AsNoTracking().Where(p => p.GroupId == groupId).OrderByDescending(p => p.CreatedAt)
            .Select(p => new PaymentDto(p.Id, p.FromUserId, p.ToUserId, p.Amount, p.CreatedAt)).ToListAsync(ct));

    private static void MapExpenses(RouteGroupBuilder g)
    {
        var e = g.MapGroup("/expenses");

        e.MapGet("/", async (Guid groupId, AppDbContext db, CancellationToken ct, int skip = 0, int take = 50) =>
        {
            var rows = await db.Expenses.AsNoTracking().Include(x => x.Shares).Where(x => x.GroupId == groupId)
                .OrderByDescending(x => x.Date).ThenByDescending(x => x.CreatedAt)
                .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
            return rows.Select(ExpenseService.ToDto).ToList();
        });

        e.MapGet("/{expenseId:guid}", async (Guid groupId, Guid expenseId, AppDbContext db, CancellationToken ct) =>
        {
            var x = await db.Expenses.AsNoTracking().Include(x => x.Shares).FirstOrDefaultAsync(x => x.Id == expenseId && x.GroupId == groupId, ct);
            return x is null ? Results.NotFound() : Results.Ok(ExpenseService.ToDto(x));
        });

        e.MapPost("/", async (Guid groupId, ExpenseRequest req, HttpContext http, ExpenseService svc, GroupNotifier notifier, CancellationToken ct) =>
        {
            var dto = await svc.CreateAsync(groupId, http.User.GetUserId(), req, ct);
            await notifier.BalancesChangedAsync(groupId, ct);
            return Results.Created($"/groups/{groupId}/expenses/{dto.Id}", dto);
        });

        e.MapPut("/{expenseId:guid}", async (Guid groupId, Guid expenseId, ExpenseRequest req, ExpenseService svc, GroupNotifier notifier, CancellationToken ct) =>
        {
            var dto = await svc.UpdateAsync(groupId, expenseId, req, ct);
            await notifier.BalancesChangedAsync(groupId, ct);
            return Results.Ok(dto);
        });

        e.MapDelete("/{expenseId:guid}", async (Guid groupId, Guid expenseId, string? rowVersion, ExpenseService svc, GroupNotifier notifier, CancellationToken ct) =>
        {
            await svc.DeleteAsync(groupId, expenseId, rowVersion, ct);
            await notifier.BalancesChangedAsync(groupId, ct);
            return Results.NoContent();
        });
    }
}
