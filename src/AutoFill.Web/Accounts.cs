using System.Net.Mail;
using AutoFill.Core;
using AutoFill.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AutoFill.Web;

/// <summary>Sign-up and sign-in. New accounts wait for an administrator, except the configured admin email.</summary>
public sealed class Accounts(AppDbContext db, Settings settings, TimeProvider clock)
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(15);
    static readonly PasswordHasher<User> Hasher = new();

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public async Task<User> RegisterAsync(string? email, string? name, string? password, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.Length is 0 or > 200 || !MailAddress.TryCreate(normalized, out var parsed) || parsed.Address != normalized)
            throw new UserFacingException("Email không hợp lệ");
        if ((password ?? "").Length < 8) throw new UserFacingException("Mật khẩu cần ít nhất 8 ký tự");
        var displayName = (name ?? "").Trim();
        if (displayName.Length > 100) displayName = displayName[..100];
        if (await db.Users.AnyAsync(u => u.Email == normalized, ct)) throw new UserFacingException("Email này đã được đăng ký");

        var isAdmin = settings.AdminEmail is not null && settings.AdminEmail == normalized;
        var user = new User
        {
            Email = normalized,
            Name = displayName,
            PasswordHash = "",
            Status = isAdmin ? UserStatus.Active : UserStatus.Pending,
            IsAdmin = isAdmin,
        };
        user.PasswordHash = Hasher.HashPassword(user, password!);
        // Every account starts with its own workspace; it can be invited into others later.
        var workspace = new Workspace { Name = displayName.Length > 0 ? displayName : normalized };
        db.Users.Add(user);
        db.Workspaces.Add(workspace);
        db.Memberships.Add(new Membership { UserId = user.Id, WorkspaceId = workspace.Id, Role = WorkspaceRole.Owner });
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>Checks the password; locks the account for a while after repeated failures.</summary>
    public async Task<User> LoginAsync(string? email, string? password, CancellationToken ct = default)
    {
        const string wrong = "Email hoặc mật khẩu không đúng";
        var normalized = NormalizeEmail(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null)
        {
            // Same work as a real check, so timing does not reveal which emails exist.
            Hasher.VerifyHashedPassword(new User { Email = "", PasswordHash = "" }, DummyHash, password ?? "");
            throw new UserFacingException(wrong, 401);
        }
        var now = clock.GetUtcNow();
        if (user.LockedUntil > now)
            throw new UserFacingException("Đăng nhập sai quá nhiều lần, hãy thử lại sau ít phút", 429);

        var check = Hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "");
        if (check == PasswordVerificationResult.Failed)
        {
            user.FailedLogins++;
            if (user.FailedLogins >= MaxFailedLogins)
            {
                user.LockedUntil = now + LockTime;
                user.FailedLogins = 0;
            }
            await db.SaveChangesAsync(ct);
            throw new UserFacingException(wrong, 401);
        }
        if (check == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hasher.HashPassword(user, password!);
        user.FailedLogins = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);

        return user.Status switch
        {
            UserStatus.Active => user,
            UserStatus.Pending => throw new UserFacingException("Tài khoản đang chờ quản trị viên kích hoạt", 403),
            _ => throw new UserFacingException("Tài khoản đã bị khóa, hãy liên hệ quản trị viên", 403),
        };
    }

    static readonly string DummyHash = Hasher.HashPassword(new User { Email = "", PasswordHash = "" }, "not-a-real-password");
}
