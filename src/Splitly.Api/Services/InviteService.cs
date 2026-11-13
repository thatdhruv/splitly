using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Splitly.Contracts;
using Splitly.Data;
using Splitly.Domain;

namespace Splitly.Api.Services;

public sealed class InviteService(AppDbContext db, TimeProvider clock)
{
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<InviteDto> CreateAsync(Guid groupId, Guid userId, CreateInviteRequest req, CancellationToken ct)
    {
        var hours = Math.Clamp(req.ExpiresInHours ?? 72, 1, 24 * 30);
        var maxUses = Math.Clamp(req.MaxUses ?? 10, 1, 100);
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        var invite = new GroupInvite
        {
            GroupId = groupId, CreatedBy = userId, TokenHash = Hash(token),
            ExpiresAt = clock.GetUtcNow().AddHours(hours), MaxUses = maxUses,
        };
        db.GroupInvites.Add(invite);
        await db.SaveChangesAsync(ct);
        return new InviteDto(invite.Id, token, invite.ExpiresAt, invite.MaxUses);
    }

    public async Task<InvitePreviewDto> PreviewAsync(string token, CancellationToken ct)
    {
        var invite = await FindUsableAsync(token, ct);
        var name = await db.Groups.Where(g => g.Id == invite.GroupId).Select(g => g.Name).FirstAsync(ct);
        return new InvitePreviewDto(name, invite.ExpiresAt);
    }

    /// <summary>Joins the user to the invite's group. Uses are incremented atomically so a limited-use link can't be over-redeemed.</summary>
    public async Task<Guid> AcceptAsync(string token, Guid userId, CancellationToken ct)
    {
        var invite = await FindUsableAsync(token, ct);

        if (await db.GroupMembers.AnyAsync(m => m.GroupId == invite.GroupId && m.UserId == userId, ct))
            return invite.GroupId; // already a member: idempotent, don't burn a use

        var now = clock.GetUtcNow();
        var claimed = await db.GroupInvites
            .Where(i => i.Id == invite.Id && i.Uses < i.MaxUses && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Uses, i => i.Uses + 1), ct);
        if (claimed == 0) throw new DomainValidationException("This invite link has expired or reached its limit.");

        db.GroupMembers.Add(new GroupMember { GroupId = invite.GroupId, UserId = userId, Role = MemberRole.Member });
        await db.SaveChangesAsync(ct);
        return invite.GroupId;
    }

    private async Task<GroupInvite> FindUsableAsync(string token, CancellationToken ct)
    {
        var hash = Hash(token);
        var invite = await db.GroupInvites.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        var now = clock.GetUtcNow();
        if (invite is null || invite.RevokedAt is not null || invite.ExpiresAt <= now || invite.Uses >= invite.MaxUses)
            throw new DomainValidationException("This invite link is invalid, expired, or has reached its limit.");
        return invite;
    }
}
