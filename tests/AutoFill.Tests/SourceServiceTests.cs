using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Data;
using AutoFill.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoFill.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SourceServiceTests(PostgresServer pg) : IClassFixture<PostgresServer>, IAsyncLifetime
{
    TestApp app = null!;
    readonly FakeScanAi ai = new();

    public async Task InitializeAsync() => app = new TestApp(await pg.CreateDatabaseAsync())
    {
        TestServices = s => s.Replace(ServiceDescriptor.Scoped<IAiClient>(_ => ai)),
    };

    public async Task DisposeAsync() => await app.DisposeAsync();

    JobWorker Worker => app.Services.GetRequiredService<JobWorker>();

    static async Task<JsonObject> UploadSourceAsync(HttpClient client, byte[] content, string name)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", name } };
        var key = (await (await client.PostAsync("/api/uploads", form)).Content.ReadFromJsonAsync<JsonObject>())!["key"]!.GetValue<string>();
        var res = await client.PostAsJsonAsync("/api/sources", new { key, name });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task An_uploaded_scan_is_read_in_the_background_and_shown_to_its_workspace_only()
    {
        var a = await app.SignedInAsync("a@example.com");
        var b = await app.SignedInAsync("b@example.com");
        var started = await UploadSourceAsync(a, SourceFixtures.Pdf("Typed cover page with enough text", null), "Văn bản đến.pdf");
        var fileId = started["fileId"]!.GetValue<Guid>();

        // Not read yet.
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/sources/{fileId}")).StatusCode);

        Assert.True(await Worker.RunOneAsync(CancellationToken.None));
        var job = await a.GetFromJsonAsync<JsonObject>($"/api/jobs/{started["jobId"]}");
        Assert.Equal("done", job!["status"]!.GetValue<string>());
        Assert.Equal(1, job["result"]!["aiPages"]!.GetValue<int>());
        Assert.False(job["result"]!["cached"]!.GetValue<bool>());

        var source = await a.GetFromJsonAsync<JsonObject>($"/api/sources/{fileId}");
        Assert.Equal("Văn bản đến.pdf", source!["summary"]!["name"]!.GetValue<string>());
        Assert.Contains("--- Trang 2 ---\nBản scan 1 của lượt 1", source["text"]!.GetValue<string>());
        Assert.Equal("pdf", source["document"]!["kind"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/sources/{fileId}")).StatusCode);
    }

    [Fact]
    public async Task The_same_file_costs_one_ai_call_per_workspace()
    {
        await app.SignedInAsync("a@example.com");
        await app.SignedInAsync("b@example.com");
        var scan = SourceFixtures.Pdf([null]);

        async Task<bool> ReadAsync(string email, string name)
        {
            await using var scope = await app.WorkspaceScopeAsync(email);
            var file = await scope.ServiceProvider.GetRequiredService<FileService>().SaveAsync(scan, name);
            return (await scope.ServiceProvider.GetRequiredService<SourceService>().ReadAsync(file)).Cached;
        }

        Assert.False(await ReadAsync("a@example.com", "scan.pdf"));
        Assert.True(await ReadAsync("a@example.com", "scan copy.pdf"));
        Assert.Single(ai.Requests);

        // Another workspace has its own cache (deleting a workspace removes its transcripts).
        Assert.False(await ReadAsync("b@example.com", "scan.pdf"));
        Assert.Equal(2, ai.Requests.Count);

        await using var check = app.Services.CreateAsyncScope();
        var rows = await check.ServiceProvider.GetRequiredService<AppDbContext>().Transcripts.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((1, 1), (r.Pages, r.AiPages)));
    }

    [Fact]
    public async Task A_cached_document_comes_back_with_sheets_tables_and_cell_addresses()
    {
        await app.SignedInAsync("a@example.com");
        await using var scope = await app.WorkspaceScopeAsync("a@example.com");
        var file = await scope.ServiceProvider.GetRequiredService<FileService>().SaveAsync(SourceFixtures.Xlsx(), "EET.xlsx");
        var sources = scope.ServiceProvider.GetRequiredService<SourceService>();

        var read = (await sources.ReadAsync(file)).Document;
        var cached = await sources.CachedAsync(file.Sha256);

        Assert.NotNull(cached);
        Assert.Equal(Documents.Ingest.SourceText.Render(read, int.MaxValue), Documents.Ingest.SourceText.Render(cached, int.MaxValue));
        Assert.Equal("B2", cached.Pages[0].Blocks[0].Rows![1][1].Ref);
    }
}
