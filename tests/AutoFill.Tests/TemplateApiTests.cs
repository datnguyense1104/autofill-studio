using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoFill.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TemplateApiTests(PostgresServer pg) : IClassFixture<PostgresServer>, IAsyncLifetime
{
    TestApp app = null!;

    // The AI picks the name in P1 (and nothing else).
    readonly FakeAi ai = new(_ => new JsonObject
    {
        ["useWhen"] = "Khi cần mời công dân đến làm việc",
        ["phrases"] = new JsonArray(new JsonObject
        {
            ["paragraph"] = 1, ["text"] = "Nguyễn Văn A", ["label"] = "Họ tên", ["valueType"] = "text", ["strategy"] = "extract",
            ["instructions"] = "Viết như trong giấy tờ", ["options"] = new JsonArray(), ["group"] = "Người", ["confidence"] = 0.95,
        }),
        ["sections"] = new JsonArray(),
    });

    public async Task InitializeAsync() => app = new TestApp(await pg.CreateDatabaseAsync())
    {
        TestServices = s => s.Replace(ServiceDescriptor.Scoped<IAiClient>(_ => ai)),
    };

    public async Task DisposeAsync() => await app.DisposeAsync();

    static async Task<string> UploadAsync(HttpClient client, byte[] content, string name)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", name } };
        return (await (await client.PostAsync("/api/uploads", form)).Content.ReadFromJsonAsync<JsonObject>())!["key"]!.GetValue<string>();
    }

    [Fact]
    public async Task A_template_is_analysed_in_the_background_and_its_slots_fill_a_copy()
    {
        var client = await app.SignedInAsync("a@example.com");
        var template = WordFixtures.Docx(WordFixtures.P("GIẤY MỜI"), WordFixtures.P("Kính mời ông Nguyễn Văn A đến Sở. Ông Nguyễn Văn A mang theo hộ chiếu."));
        var key = await UploadAsync(client, template, "Giấy mời.docx");

        var started = await (await client.PostAsJsonAsync("/api/templates", new { key, name = "Giấy mời.docx", notes = "Mẫu giấy mời" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(await app.Services.GetRequiredService<JobWorker>().RunOneAsync(CancellationToken.None));

        var job = await client.GetFromJsonAsync<JsonObject>($"/api/jobs/{started!["jobId"]}");
        Assert.Equal("done", job!["status"]!.GetValue<string>());
        var analysis = job["result"]!["analysis"]!;
        Assert.Equal("word", analysis["kind"]!.GetValue<string>());
        var slot = analysis["slots"]!.AsArray().Single()!;
        Assert.Equal(("Họ tên", "phrase", 2), (slot["label"]!.GetValue<string>(), slot["locations"]![0]!["kind"]!.GetValue<string>(), slot["locations"]!.AsArray().Count));
        Assert.Contains("Mẫu giấy mời", ai.Requests.Single().Input);

        // The slots come back as they were sent and fill the template.
        var res = await client.PostAsJsonAsync($"/api/templates/{job["result"]!["templateFileId"]}/fill", new
        {
            slots = analysis["slots"],
            values = new JsonObject { ["s1"] = new JsonObject { ["text"] = "Trần Thị B" } },
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Kính mời ông Trần Thị B đến Sở. Ông Trần Thị B mang theo hộ chiếu.", WordFixtures.Texts(await res.Content.ReadAsByteArrayAsync())[1]);
    }

    [Fact]
    public async Task Only_word_and_excel_files_can_be_templates()
    {
        var client = await app.SignedInAsync("a@example.com");
        var key = await UploadAsync(client, SourceFixtures.Pdf("A pdf is a source, not a template"), "mau.pdf");

        var res = await client.PostAsJsonAsync("/api/templates", new { key, name = "mau.pdf" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Word", (await res.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>());
    }
}
