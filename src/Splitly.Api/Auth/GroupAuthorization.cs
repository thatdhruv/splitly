using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Splitly.Data;

namespace Splitly.Api.Auth;

public static class Policies
{
    public const string GroupMember = "GroupMember";
}

public sealed class GroupMemberRequirement : IAuthorizationRequirement;

public static class UserExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new UnauthorizedAccessException("Authenticated principal has no user id.");
}

/// <summary>
/// Single choke point for per-group access: the caller must be a member of the group named by the
/// <c>groupId</c> route value. Non-members and unknown groups are indistinguishable (both 403).
/// </summary>
public sealed class GroupMemberHandler(AppDbContext db) : AuthorizationHandler<GroupMemberRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, GroupMemberRequirement requirement)
    {
        if (context.Resource is not HttpContext http
            || !Guid.TryParse(http.GetRouteValue("groupId")?.ToString(), out var groupId)
            || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return;

        if (await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId, http.RequestAborted))
            context.Succeed(requirement);
    }
}
