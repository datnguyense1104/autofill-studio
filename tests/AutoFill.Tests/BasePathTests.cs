using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AutoFill.Tests;

/// <summary>Published under a path (https://ntdat.qzz.io/auto-fill/): the tunnel passes the prefix on.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BasePathTests(PostgresServer pg) : IClassFixture<PostgresServer>, IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = new TestApp(await pg.CreateDatabaseAsync())
    {
        Settings = { ["BASE_PATH"] = "auto-fill/" },
    };

    public async Task DisposeAsync() => await app.DisposeAsync();

    [Fact]
    public async Task The_app_works_under_its_path_prefix()
    {
        var client = app.Browser();

        var bare = await client.GetAsync("/auto-fill");
        Assert.Equal(HttpStatusCode.Redirect, bare.StatusCode);
        Assert.Equal("/auto-fill/", bare.Headers.Location!.OriginalString);

        var page = await client.GetAsync("/auto-fill/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("AutoFill Studio", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/auto-fill/health")).StatusCode);

        (await client.PostAsJsonAsync("/auto-fill/api/auth/register", new { email = "admin@example.com", name = "A", password = "password-123" })).EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync("/auto-fill/api/auth/login", new { email = "admin@example.com", password = "password-123" });
        login.EnsureSuccessStatusCode();
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.Contains("path=/auto-fill", StringComparison.OrdinalIgnoreCase));
        var me = await client.GetFromJsonAsync<JsonObject>("/auto-fill/api/auth/me");
        Assert.Equal("admin@example.com", me!["email"]!.GetValue<string>());
    }
}
