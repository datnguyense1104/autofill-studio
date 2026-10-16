using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoFill.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AuthAndWorkspaceTests(PostgresServer pg) : IClassFixture<PostgresServer>, IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = new TestApp(await pg.CreateDatabaseAsync());
    public async Task DisposeAsync() => await app.DisposeAsync();

    [Fact]
    public async Task New_account_waits_for_admin_and_admin_email_is_active_at_once()
    {
        var client = app.Browser();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new { email = "User@Example.com", name = "Lan", password = "password-123" });
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "user@example.com", password = "password-123" });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Contains("chờ quản trị viên", (await login.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>());

        var admin = await app.SignedInAsync("admin@example.com");
        var me = await admin.GetFromJsonAsync<JsonObject>("/api/auth/me");
        Assert.True(me!["isAdmin"]!.GetValue<bool>());
        Assert.Equal("server", me["uploadMode"]!.GetValue<string>());
        Assert.Single(me["workspaces"]!.AsArray());

        var users = await admin.GetFromJsonAsync<JsonArray>("/api/admin/users");
        var pending = users!.First(u => u!["email"]!.GetValue<string>() == "user@example.com")!;
        Assert.Equal("pending", pending["status"]!.GetValue<string>());
        (await admin.PostAsJsonAsync($"/api/admin/users/{pending["id"]}/status", new { status = "active" })).EnsureSuccessStatusCode();

        login = await client.PostAsJsonAsync("/api/auth/login", new { email = "user@example.com", password = "password-123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Disabling_an_account_ends_its_session()
    {
        var admin = await app.SignedInAsync("admin@example.com");
        var user = await app.SignedInAsync("lan@example.com");
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/auth/me")).StatusCode);

        var users = await admin.GetFromJsonAsync<JsonArray>("/api/admin/users");
        var id = users!.First(u => u!["email"]!.GetValue<string>() == "lan@example.com")!["id"];
        (await admin.PostAsJsonAsync($"/api/admin/users/{id}/status", new { status = "disabled" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        await app.SignedInAsync("admin@example.com");
        var client = app.Browser();
        for (var i = 0; i < 5; i++)
        {
            var wrong = await client.PostAsJsonAsync("/api/auth/login", new { email = "admin@example.com", password = "nope-nope" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        var right = await client.PostAsJsonAsync("/api/auth/login", new { email = "admin@example.com", password = "password-123" });
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
    }

    [Fact]
    public async Task Api_needs_a_login()
    {
        var anonymous = app.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/jobs/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Workspaces_do_not_see_each_others_files_or_jobs()
    {
        var a = await app.SignedInAsync("a@example.com");
        var b = await app.SignedInAsync("b@example.com");

        Guid fileId, jobId;
        await using (var scope = await app.WorkspaceScopeAsync("a@example.com"))
        {
            var file = await scope.ServiceProvider.GetRequiredService<FileService>().SaveAsync("%PDF-1.4 a"u8.ToArray(), "Công hàm.pdf");
            fileId = file.Id;
            jobId = (await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueAsync("noop", new { }, null)).Id;
        }

        await using (var scope = await app.WorkspaceScopeAsync("b@example.com"))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.Files.CountAsync());
            Assert.Equal(0, await db.Jobs.CountAsync());
            var err = await Assert.ThrowsAsync<UserFacingException>(() => scope.ServiceProvider.GetRequiredService<FileService>().GetAsync(fileId));
            Assert.Equal(404, err.Status);
        }

        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/jobs/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/jobs/{jobId}")).StatusCode);

        // Selecting someone else's workspace is refused.
        await using (var scope = await app.WorkspaceScopeAsync("a@example.com"))
        {
            var wsA = scope.ServiceProvider.GetRequiredService<WorkspaceScope>().WorkspaceId;
            Assert.Equal(HttpStatusCode.Forbidden, (await b.PostAsync($"/api/workspaces/{wsA}/select", null)).StatusCode);
        }
    }

    [Fact]
    public async Task Uploads_are_stored_and_only_their_workspace_can_take_them()
    {
        var a = await app.SignedInAsync("a@example.com");
        await app.SignedInAsync("b@example.com");

        using var form = new MultipartFormDataContent { { new ByteArrayContent("%PDF-1.4 scan"u8.ToArray()), "file", "Văn bản đến.pdf" } };
        var res = await a.PostAsync("/api/uploads", form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var key = (await res.Content.ReadFromJsonAsync<JsonObject>())!["key"]!.GetValue<string>();
        Assert.Matches("^uploads/[0-9a-f]{32}/[0-9a-f]{32}\\.pdf$", key);

        await using (var scope = await app.WorkspaceScopeAsync("b@example.com"))
        {
            await Assert.ThrowsAsync<UserFacingException>(() =>
                scope.ServiceProvider.GetRequiredService<FileService>().TakeUploadAsync(key, "x.pdf"));
        }
        await using (var scope = await app.WorkspaceScopeAsync("a@example.com"))
        {
            var files = scope.ServiceProvider.GetRequiredService<FileService>();
            var file = await files.TakeUploadAsync(key, "Văn bản đến.pdf");
            Assert.Equal("Văn bản đến.pdf", file.Name);
            Assert.Equal("%PDF-1.4 scan"u8.ToArray(), await files.ReadAsync(file));
            // The upload is gone once taken; the same content saved again is the same file.
            await Assert.ThrowsAsync<UserFacingException>(() => files.TakeUploadAsync(key, "x.pdf"));
            Assert.Equal(file.Id, (await files.SaveAsync("%PDF-1.4 scan"u8.ToArray(), "copy.pdf")).Id);
        }

        using var bad = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "file", "virus.exe" } };
        var refused = await a.PostAsync("/api/uploads", bad);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Pages_are_served_with_security_headers()
    {
        var client = app.Browser();
        foreach (var page in new[] { "/", "/login.html", "/admin.html", "/app.js", "/styles.css" })
        {
            var res = await client.GetAsync(page);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("default-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        }
        Assert.Contains("AutoFill Studio", await client.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Every_table_has_row_level_security()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var open = await db.Database.SqlQueryRaw<string>(
            "SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = 'public' AND NOT rowsecurity").ToListAsync();
        Assert.Empty(open);
    }
}
