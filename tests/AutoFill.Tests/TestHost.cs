using System.Net;
using System.Net.Http.Json;
using AutoFill.Core;
using AutoFill.Data;
using AutoFill.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MysticMind.PostgresEmbed;
using Npgsql;

namespace AutoFill.Tests;

/// <summary>A real PostgreSQL server (binaries downloaded once, then cached) for one test class.</summary>
public sealed class PostgresServer : IAsyncLifetime
{
    PgServer? server;
    public int Port { get; private set; }

    const string Version = "16.4.0";
    static readonly string InstancesDir = Path.Combine(AppContext.BaseDirectory, "pg_embed");
    /// <summary>An initialized instance kept between runs: copying it takes ~2 s, a fresh initdb ~18 s.</summary>
    static readonly Guid GoldenId = new("00000000-0000-0000-0000-00000000a0f1");
    static readonly SemaphoreSlim GoldenLock = new(1, 1);
    readonly Guid instanceId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        // Folders an earlier run could not delete (see DisposeAsync).
        foreach (var old in Directory.Exists(InstancesDir) ? Directory.GetDirectories(InstancesDir) : [])
            if (Guid.TryParse(Path.GetFileName(old), out var id) && id != GoldenId) TryDelete(old);
        var dir = Path.Combine(InstancesDir, instanceId.ToString());
        await CopyGoldenAsync(dir);
        Port = FreePort();
        server = new PgServer(Version, port: Port, instanceId: instanceId);
        try
        {
            await server.StartAsync();
        }
        catch (Exception)
        {
            // The copy would not start (another OS's file rules...): a fresh instance always does, only slower.
            TryDelete(dir);
            Port = FreePort();
            server = new PgServer(Version, port: Port, instanceId: instanceId);
            await server.StartAsync();
        }
    }

    /// <summary>Copies the golden instance (creating it once: start and stop a server, then keep its folder).</summary>
    static async Task CopyGoldenAsync(string target)
    {
        var golden = Path.Combine(InstancesDir, GoldenId.ToString());
        await GoldenLock.WaitAsync();
        try
        {
            if (!Directory.Exists(Path.Combine(golden, "data")))
            {
                var temp = Guid.NewGuid();
                var first = new PgServer(Version, port: FreePort(), instanceId: temp);
                await first.StartAsync();
                await first.StopAsync();
                // Renamed only once complete, so a crash never leaves a half-made golden instance.
                Directory.Move(Path.Combine(InstancesDir, temp.ToString()), golden);
            }
        }
        finally
        {
            GoldenLock.Release();
        }
        // Empty folders too: the server refuses to start without pg_notify, pg_twophase...
        // On Linux/macOS the data folder must be private to its owner (0700), or the server refuses it.
        foreach (var dir in Directory.GetDirectories(golden, "*", SearchOption.AllDirectories).Prepend(golden))
        {
            var path = Path.Combine(target, Path.GetRelativePath(golden, dir));
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var files = Directory.GetFiles(golden, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "postmaster.pid").ToList();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            f => File.Copy(f, Path.Combine(target, Path.GetRelativePath(golden, f))));
    }

    /// <summary>
    /// The instance folder is deleted here, not by the package: on Windows the server's files stay
    /// locked for a while after it stops, and the package's cleanup then fails the whole run.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (server is null) return;
        await server.StopAsync();
        TryDelete(Path.Combine(InstancesDir, instanceId.ToString()));
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception err) when (err is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A brand-new empty database, so tests never see each other's rows.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";
        await ExecuteAsync(Port, $"CREATE DATABASE {name}");
        return $"Host=localhost;Port={Port};Username=postgres;Database={name}";
    }

    static async Task ExecuteAsync(int port, string sql)
    {
        await using var conn = new NpgsqlConnection($"Host=localhost;Port={port};Username=postgres;Database=postgres");
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// Database test classes run one after another, each with its own server (IClassFixture): on
/// Windows the embedded server stalls queries for good once it has created about 16 databases.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection
{
    public const string Name = "postgres";
}

/// <summary>The real app on a fresh database, with disk file storage, no AI key and no background workers.</summary>
public sealed class TestApp(string connectionString, string adminEmail = "admin@example.com") : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "autofill-tests", Guid.NewGuid().ToString("N"));
    public Action<IServiceCollection>? TestServices { get; init; }
    /// <summary>Extra settings (environment variables) for this app.</summary>
    public Dictionary<string, string> Settings { get; init; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DATABASE_URL", connectionString);
        builder.UseSetting("FILE_STORE", "disk");
        builder.UseSetting("DATA_DIR", DataDir);
        builder.UseSetting("ADMIN_EMAIL", adminEmail);
        builder.UseSetting("WORKERS", "0");
        builder.UseSetting("OPENAI_API_KEY", "");
        builder.UseSetting("COOKIE_SECURE", "false");
        // The developer's .env (loaded by Program) must not change the app under test.
        builder.UseSetting("BASE_PATH", "");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
        builder.ConfigureTestServices(s => TestServices?.Invoke(s));
    }

    /// <summary>A browser-like client that keeps its login cookie.</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    /// <summary>Registers an account; activates it through the admin unless it is the admin.</summary>
    public async Task<HttpClient> SignedInAsync(string email, string password = "password-123")
    {
        var client = Browser();
        (await client.PostAsJsonAsync("/api/auth/register", new { email, name = email.Split('@')[0], password })).EnsureSuccessStatusCode();
        if (email != adminEmail)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = db.Users.Single(u => u.Email == email);
            user.Status = UserStatus.Active;
            await db.SaveChangesAsync();
        }
        (await client.PostAsJsonAsync("/api/auth/login", new { email, password })).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>A DI scope working inside the given user's (first) workspace, as a request or a job would.</summary>
    public async Task<AsyncServiceScope> WorkspaceScopeAsync(string email)
    {
        var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = db.Users.Single(u => u.Email == email).Id;
        scope.ServiceProvider.GetRequiredService<WorkspaceScope>().WorkspaceId =
            db.Memberships.Where(m => m.UserId == userId).OrderBy(m => m.CreatedAt).First().WorkspaceId;
        await Task.CompletedTask;
        return scope;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(DataDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}
