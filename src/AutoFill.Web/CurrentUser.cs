using System.Security.Claims;
using AutoFill.Core;
using AutoFill.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace AutoFill.Web;

/// <summary>Who is calling and in which workspace; checked against the database on every API request.</summary>
public sealed record CurrentUser(Guid Id, bool IsAdmin, Guid? WorkspaceId, WorkspaceRole? Role)
{
    const string ItemKey = nameof(CurrentUser);
    public const string WorkspaceClaim = "ws";

    public static CurrentUser From(HttpContext ctx) =>
        ctx.Items[ItemKey] as CurrentUser ?? throw new UserFacingException("Chưa đăng nhập", 401);

    public Guid RequireWorkspace() => WorkspaceId ?? throw new UserFacingException("Chưa chọn không gian làm việc", 403);

    /// <summary>Recipes and templates are changed by owners and editors; reviewers only run and review.</summary>
    public void RequireEditor()
    {
        RequireWorkspace();
        if (Role is not (WorkspaceRole.Owner or WorkspaceRole.Editor))
            throw new UserFacingException("Chỉ chủ sở hữu hoặc người soạn mới sửa được công thức", 403);
    }

    public static Task SignInAsync(HttpContext ctx, Guid userId, Guid? workspaceId)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];
        if (workspaceId is { } ws) claims.Add(new(WorkspaceClaim, ws.ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>
    /// For API requests with a login cookie: the account must still be active and still a member of
    /// the workspace in the cookie. Otherwise the request continues as anonymous (and protected
    /// endpoints answer 401). Sets the workspace that the database filters by.
    /// </summary>
    public static async Task Resolve(HttpContext ctx, Func<Task> next)
    {
        if (ctx.Request.Path.StartsWithSegments("/api") && ctx.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
            Guid? wanted = Guid.TryParse(ctx.User.FindFirstValue(WorkspaceClaim), out var ws) ? ws : null;
            var row = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId && u.Status == UserStatus.Active)
                .Select(u => new
                {
                    u.IsAdmin,
                    Member = db.Memberships.Where(m => m.UserId == u.Id && m.WorkspaceId == wanted).Select(m => (WorkspaceRole?)m.Role).FirstOrDefault(),
                })
                .FirstOrDefaultAsync(ctx.RequestAborted);
            if (row is null)
            {
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
            else
            {
                var workspaceId = row.Member is null ? null : wanted;
                ctx.Items[ItemKey] = new CurrentUser(userId, row.IsAdmin, workspaceId, row.Member);
                ((WorkspaceScope)ctx.RequestServices.GetRequiredService<IWorkspaceScope>()).WorkspaceId = workspaceId;
            }
        }
        await next();
    }
}
