using System.Text.Json;
using AutoFill.Core;
using AutoFill.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace AutoFill.Web;

public sealed record RegisterBody(string? Email, string? Name, string? Password);
public sealed record LoginBody(string? Email, string? Password);
public sealed record UploadBody(string? Name);
public sealed record StatusBody(string? Status);

public static class Endpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/register", async (RegisterBody body, Accounts accounts, CancellationToken ct) =>
        {
            var user = await accounts.RegisterAsync(body.Email, body.Name, body.Password, ct);
            return Results.Ok(new
            {
                message = user.Status == UserStatus.Active
                    ? "Đăng ký thành công, anh/chị có thể đăng nhập ngay."
                    : "Đăng ký thành công. Tài khoản cần quản trị viên kích hoạt trước khi dùng.",
            });
        });

        auth.MapPost("/login", async (LoginBody body, Accounts accounts, AppDbContext db, HttpContext ctx, CancellationToken ct) =>
        {
            var user = await accounts.LoginAsync(body.Email, body.Password, ct);
            var workspaceId = await db.Memberships.Where(m => m.UserId == user.Id)
                .OrderBy(m => m.CreatedAt).Select(m => (Guid?)m.WorkspaceId).FirstOrDefaultAsync(ct);
            await CurrentUser.SignInAsync(ctx, user.Id, workspaceId);
            return Results.Ok(new { ok = true });
        });

        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        });

        auth.MapGet("/me", async (HttpContext ctx, AppDbContext db, IFileStore store, CancellationToken ct) =>
        {
            var me = CurrentUser.From(ctx);
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me.Id, ct);
            var workspaces = await db.Memberships.AsNoTracking().Where(m => m.UserId == me.Id)
                .Join(db.Workspaces, m => m.WorkspaceId, w => w.Id, (m, w) => new { w.Id, w.Name, role = m.Role.ToString(), m.CreatedAt })
                .OrderBy(w => w.CreatedAt).ToListAsync(ct);
            return Results.Ok(new
            {
                id = user.Id, email = user.Email, name = user.Name, isAdmin = user.IsAdmin,
                workspaceId = me.WorkspaceId,
                workspaces = workspaces.Select(w => new { w.Id, w.Name, w.role }),
                uploadMode = store is SupabaseFileStore ? "direct" : "server",
            });
        }).RequireAuthorization();
    }

    public static void MapWorkspaces(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/workspaces/{id:guid}/select", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var me = CurrentUser.From(ctx);
            if (!await db.Memberships.AnyAsync(m => m.UserId == me.Id && m.WorkspaceId == id, ct))
                throw new UserFacingException("Anh/chị không thuộc không gian làm việc này", 403);
            await CurrentUser.SignInAsync(ctx, me.Id, id);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();
    }

    public static void MapUploads(this IEndpointRouteBuilder app)
    {
        var uploads = app.MapGroup("/api/uploads").RequireAuthorization();

        // Large files go straight from the browser to storage through a one-off signed URL.
        uploads.MapPost("/direct", async (UploadBody body, FileService files, CancellationToken ct) =>
        {
            var (key, url) = await files.CreateUploadAsync(body.Name ?? "", ct);
            if (url is null) throw new UserFacingException("Máy chủ này nhận file trực tiếp, không cần link tải lên");
            return Results.Ok(new { key, url });
        });

        // Disk storage (development): the file comes through this server.
        uploads.MapPost("/", async (IFormFile file, FileService files, CancellationToken ct) =>
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            return Results.Ok(new { key = await files.ReceiveUploadAsync(buffer.ToArray(), file.FileName, ct) });
        }).DisableAntiforgery();
    }

    static object JobView(Job j) => new
    {
        id = j.Id, type = j.Type, status = j.Status.ToString().ToLowerInvariant(),
        progress = j.Progress is null ? null : JsonDocument.Parse(j.Progress).RootElement.Clone() as object,
        result = j.Result is null ? null : JsonDocument.Parse(j.Result).RootElement.Clone() as object,
        error = j.Error, createdAt = j.CreatedAt, updatedAt = j.UpdatedAt,
    };

    public static void MapJobs(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/jobs").RequireAuthorization();

        jobs.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) is { } job
                ? Results.Ok(JobView(job))
                : Results.NotFound(new { error = "Không tìm thấy việc cần xử lý" }));

        // Server-Sent Events: one message each time the job changes, until it is done or failed.
        jobs.MapGet("/{id:guid}/events", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            string? last = null;
            while (!ct.IsCancellationRequested)
            {
                var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
                if (job is null)
                {
                    await ctx.Response.WriteAsync("event: error\ndata: {\"error\":\"Không tìm thấy việc cần xử lý\"}\n\n", ct);
                    return;
                }
                var json = JsonSerializer.Serialize(JobView(job));
                if (json != last)
                {
                    await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    last = json;
                }
                if (job.Status is JobStatus.Done or JobStatus.Failed) return;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        });
    }

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization().AddEndpointFilter(async (context, next) =>
            CurrentUser.From(context.HttpContext).IsAdmin
                ? await next(context)
                : Results.Json(new { error = "Chỉ quản trị viên mới làm được việc này" }, statusCode: 403));

        admin.MapGet("/users", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Users.AsNoTracking().OrderByDescending(u => u.CreatedAt)
                .Select(u => new { u.Id, u.Email, u.Name, status = u.Status.ToString().ToLowerInvariant(), u.IsAdmin, u.CreatedAt })
                .ToListAsync(ct)));

        admin.MapPost("/users/{id:guid}/status", async (Guid id, StatusBody body, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            if (!Enum.TryParse<UserStatus>(body.Status, ignoreCase: true, out var status))
                throw new UserFacingException("Trạng thái không hợp lệ");
            if (id == CurrentUser.From(ctx).Id) throw new UserFacingException("Không thể đổi trạng thái tài khoản của chính mình");
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new UserFacingException("Không tìm thấy tài khoản", 404);
            user.Status = status;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok = true });
        });
    }
}
