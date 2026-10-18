using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Web;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AutoFill.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RunApiTests(PostgresServer pg) : IClassFixture<PostgresServer>, IAsyncLifetime
{
    TestApp app = null!;
    bool aiFails;

    FakeAi Ai => new(r =>
    {
        if (aiFails) throw new UserFacingException("AI từ chối xử lý: thử nghiệm");
        static string[] Enum(JsonNode schema, string path) =>
            path.Split('.').Aggregate(schema, (n, p) => n[p]!)["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        return r.Purpose switch
        {
            "run-missing" => new JsonObject { ["items"] = new JsonArray() },
            "recipe-suggest" => new JsonObject
            {
                ["suggestions"] = new JsonArray(new JsonObject
                    { ["recipeId"] = Enum(r.Schema, "properties.suggestions.items.properties.recipeId")[0], ["score"] = 0.9, ["reason"] = "Có tên người" }),
            },
            _ => new JsonObject
            {
                ["values"] = new JsonArray([.. Enum(r.Schema, "properties.values.items.properties.slotId").Select(id => (JsonNode)new JsonObject
                {
                    ["slotId"] = id, ["value"] = id == "name" ? "Typed Person" : "", ["status"] = "found", ["confidence"] = 0.9, ["note"] = "",
                    ["rows"] = new JsonArray([.. (r.Input.Contains("Các dòng") ? new[] { 3, 4 } : []).Select(n => (JsonNode)new JsonObject { ["row"] = n, ["value"] = "Y" })]),
                    ["evidence"] = new JsonArray(new JsonObject { ["doc"] = 1, ["page"] = 1, ["quote"] = "Typed Person" }),
                })]),
            },
        };
    });

    public async Task InitializeAsync() => app = new TestApp(await pg.CreateDatabaseAsync())
    {
        TestServices = s => s.Replace(ServiceDescriptor.Scoped<IAiClient>(_ => Ai)),
    };

    public async Task DisposeAsync() => await app.DisposeAsync();

    JobWorker Worker => app.Services.GetRequiredService<JobWorker>();

    static async Task<Guid> SourceAsync(HttpClient client, byte[] content, string name)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", name } };
        var key = (await (await client.PostAsync("/api/uploads", form)).Content.ReadFromJsonAsync<JsonObject>())!["key"]!.GetValue<string>();
        return (await (await client.PostAsJsonAsync("/api/sources", new { key, name })).Content.ReadFromJsonAsync<JsonObject>())!["fileId"]!.GetValue<Guid>();
    }

    async Task<Guid> RunAllJobsAndGetRecipeAsync(HttpClient client, byte[] template, string name, JsonArray slots)
    {
        var templateId = await SourceAsync(client, template, name); // any workspace file can be a template
        while (await Worker.RunOneAsync(CancellationToken.None)) { }
        var res = await client.PostAsJsonAsync("/api/recipes", new { name = "Giấy mời", templateFileId = templateId, slots, useWhen = "Khi có thư mời", notes = "Viết hoa họ tên" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
    }

    static JsonArray WordSlots() => new(new JsonObject
    {
        ["id"] = "name", ["label"] = "Họ tên", ["strategy"] = "extract", ["valueType"] = "text",
        ["locations"] = new JsonArray(new JsonObject { ["kind"] = "phrase", ["paragraph"] = 0, ["start"] = 9, ["length"] = 12 }),
    });

    [Fact]
    public async Task A_run_fills_every_slot_with_evidence_and_waits_for_review()
    {
        var a = await app.SignedInAsync("a@example.com");
        var b = await app.SignedInAsync("b@example.com");
        var recipeId = await RunAllJobsAndGetRecipeAsync(a, WordFixtures.Docx(WordFixtures.P("Kính mời Nguyễn Văn A")), "moi.docx", WordSlots());
        var source = await SourceAsync(a, SourceFixtures.Pdf("Invitation for Typed Person on Monday"), "thu.pdf");

        var started = await (await a.PostAsJsonAsync("/api/runs", new { recipeId, sourceFileIds = new[] { source } })).Content.ReadFromJsonAsync<JsonObject>();
        while (await Worker.RunOneAsync(CancellationToken.None)) { }

        var run = await a.GetFromJsonAsync<JsonObject>($"/api/runs/{started!["runId"]}");
        Assert.Equal("NeedsReview", run!["status"]!.GetValue<string>());
        var value = run["values"]!.AsArray().Single()!;
        Assert.Equal("Typed Person", value["value"]!["text"]!.GetValue<string>());
        Assert.True(value["evidence"]![0]!["found"]!.GetValue<bool>());
        Assert.Empty(value["warnings"]!.AsArray());
        Assert.Equal("thu.pdf", run["sources"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(1, run["recipe"]!["version"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/runs/{started["runId"]}")).StatusCode);
        Assert.Single((await a.GetFromJsonAsync<JsonArray>("/api/runs"))!);
        Assert.Empty((await b.GetFromJsonAsync<JsonArray>("/api/runs"))!);

        // A new version does not change the run that used the old one.
        var template = run["recipe"]!["templateFileId"]!.GetValue<Guid>();
        (await a.PostAsJsonAsync($"/api/recipes/{recipeId}/versions", new { templateFileId = template, slots = WordSlots() })).EnsureSuccessStatusCode();
        Assert.Equal(2, (await a.GetFromJsonAsync<JsonObject>($"/api/recipes/{recipeId}"))!["version"]!["number"]!.GetValue<int>());
        Assert.Equal(1, (await a.GetFromJsonAsync<JsonObject>($"/api/runs/{started["runId"]}"))!["recipe"]!["version"]!.GetValue<int>());

        // Suggest a recipe for the same source.
        var suggest = await (await a.PostAsJsonAsync("/api/recipes/suggest", new { sourceFileIds = new[] { source } })).Content.ReadFromJsonAsync<JsonObject>();
        while (await Worker.RunOneAsync(CancellationToken.None)) { }
        var job = await a.GetFromJsonAsync<JsonObject>($"/api/jobs/{suggest!["jobId"]}");
        Assert.Equal(recipeId, job!["result"]!["suggestions"]![0]!["recipeId"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task A_reviewed_value_goes_into_the_exported_file_and_empty_required_slots_block_export()
    {
        var a = await app.SignedInAsync("a@example.com");
        var recipeId = await RunAllJobsAndGetRecipeAsync(a, WordFixtures.Docx(WordFixtures.P("Kính mời Nguyễn Văn A")), "moi.docx", WordSlots());
        var source = await SourceAsync(a, SourceFixtures.Pdf("Invitation for Typed Person on Monday"), "thu.pdf");
        var runId = (await (await a.PostAsJsonAsync("/api/runs", new { recipeId, sourceFileIds = new[] { source } })).Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<Guid>();
        while (await Worker.RunOneAsync(CancellationToken.None)) { }

        // Cleared by the reviewer: export is refused.
        (await a.PutAsJsonAsync($"/api/runs/{runId}/values/name", new { value = new { text = " " } })).EnsureSuccessStatusCode();
        var refused = await a.GetAsync($"/api/runs/{runId}/export");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Họ tên", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>());

        (await a.PutAsJsonAsync($"/api/runs/{runId}/values/name", new { value = new { text = "TYPED PERSON" } })).EnsureSuccessStatusCode();
        var file = await a.GetAsync($"/api/runs/{runId}/export");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("Kính mời TYPED PERSON", WordFixtures.Texts(await file.Content.ReadAsByteArrayAsync()).Single());
        Assert.Equal("Done", (await a.GetFromJsonAsync<JsonObject>($"/api/runs/{runId}"))!["status"]!.GetValue<string>());

        // Revise asks the AI again and drops the reviewer's value.
        var revised = await (await a.PostAsJsonAsync($"/api/runs/{runId}/values/name/revise", new { instruction = "Viết thường" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Typed Person", revised!["value"]!["text"]!.GetValue<string>());
        Assert.Null(revised["reviewedValue"]);
    }

    [Fact]
    public async Task A_deleted_recipe_leaves_the_list_and_new_runs_but_its_past_runs_still_open()
    {
        var a = await app.SignedInAsync("a@example.com");
        var recipeId = await RunAllJobsAndGetRecipeAsync(a, WordFixtures.Docx(WordFixtures.P("Kính mời Nguyễn Văn A")), "moi.docx", WordSlots());
        var source = await SourceAsync(a, SourceFixtures.Pdf("Invitation for Typed Person on Monday"), "thu.pdf");
        var runId = (await (await a.PostAsJsonAsync("/api/runs", new { recipeId, sourceFileIds = new[] { source } })).Content.ReadFromJsonAsync<JsonObject>())!["runId"];
        while (await Worker.RunOneAsync(CancellationToken.None)) { }

        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/recipes/{recipeId}")).StatusCode);

        Assert.Empty((await a.GetFromJsonAsync<JsonArray>("/api/recipes"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/recipes/{recipeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.PostAsJsonAsync("/api/runs", new { recipeId, sourceFileIds = new[] { source } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.DeleteAsync($"/api/recipes/{recipeId}")).StatusCode);
        var past = await a.GetFromJsonAsync<JsonObject>($"/api/runs/{runId}");
        Assert.Equal("Typed Person", past!["values"]![0]!["value"]!["text"]!.GetValue<string>());
        Assert.Single((await a.GetFromJsonAsync<JsonArray>("/api/runs"))!);
    }

    [Fact]
    public async Task Reviewers_cannot_change_recipes()
    {
        var owner = await app.SignedInAsync("a@example.com");
        var templateId = await SourceAsync(owner, WordFixtures.Docx(WordFixtures.P("Kính mời Nguyễn Văn A")), "moi.docx");
        await using (var scope = await app.WorkspaceScopeAsync("a@example.com"))
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            var m = db.Memberships.Single();
            m.Role = WorkspaceRole.Reviewer;
            await db.SaveChangesAsync();
        }
        var res = await owner.PostAsJsonAsync("/api/recipes", new { name = "X", templateFileId = templateId, slots = WordSlots() });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task A_recipe_must_be_runnable_before_it_is_saved()
    {
        var a = await app.SignedInAsync("a@example.com");
        var templateId = await SourceAsync(a, WordFixtures.Docx(WordFixtures.P("Kính mời Nguyễn Văn A")), "moi.docx");
        async Task<string> ErrorAsync(object body)
        {
            var res = await a.PostAsJsonAsync("/api/recipes", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return (await res.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>();
        }

        var twice = new JsonArray(WordSlots()[0]!.DeepClone(), WordSlots()[0]!.DeepClone());
        Assert.Contains("trùng", await ErrorAsync(new { name = "X", templateFileId = templateId, slots = twice }));
        var cell = new JsonArray(new JsonObject { ["id"] = "c", ["label"] = "Ô", ["locations"] = new JsonArray(new JsonObject { ["kind"] = "cell", ["sheet"] = "S", ["cell"] = "A1" }) });
        Assert.Contains("không hợp với mẫu Word", await ErrorAsync(new { name = "X", templateFileId = templateId, slots = cell }));
        Assert.Contains("tên", await ErrorAsync(new { name = " ", templateFileId = templateId, slots = WordSlots() }));
        var rule = new[] { new { type = "dateAfter", slot = "name", other = "nope" } };
        Assert.Contains("không có trong công thức", await ErrorAsync(new { name = "X", templateFileId = templateId, slots = WordSlots(), rules = rule }));
    }

    [Fact]
    public async Task A_run_whose_job_fails_shows_the_error()
    {
        var a = await app.SignedInAsync("a@example.com");
        var recipeId = await RunAllJobsAndGetRecipeAsync(a, WordFixtures.Docx(WordFixtures.P("Kính mời Nguyễn Văn A")), "moi.docx", WordSlots());
        var source = await SourceAsync(a, SourceFixtures.Pdf("Invitation for Typed Person on Monday"), "thu.pdf");
        while (await Worker.RunOneAsync(CancellationToken.None)) { }
        aiFails = true;

        var started = await (await a.PostAsJsonAsync("/api/runs", new { recipeId, sourceFileIds = new[] { source } })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(await Worker.RunOneAsync(CancellationToken.None));

        var run = await a.GetFromJsonAsync<JsonObject>($"/api/runs/{started!["runId"]}");
        Assert.Equal(("failed", "AI từ chối xử lý: thử nghiệm"), (run!["status"]!.GetValue<string>(), run["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Row_rules_send_each_row_label_from_the_template()
    {
        var a = await app.SignedInAsync("a@example.com");
        byte[] template;
        using (var book = new XLWorkbook())
        {
            var ws = book.AddWorksheet("Q");
            ws.Cell("B3").Value = "Có kiểm toán nội bộ không?";
            ws.Cell("B4").Value = "Có thay đổi kế toán trưởng không?";
            using var ms = new MemoryStream();
            book.SaveAs(ms);
            template = ms.ToArray();
        }
        var slots = new JsonArray(new JsonObject
        {
            ["id"] = "yn", ["label"] = "Y/N", ["valueType"] = "boolean", ["options"] = new JsonArray("Y", "N"),
            ["locations"] = new JsonArray(new JsonObject { ["kind"] = "rows", ["sheet"] = "Q", ["labelColumn"] = "B", ["column"] = "I", ["rows"] = new JsonArray(3, 4) }),
        });
        var recipeId = await RunAllJobsAndGetRecipeAsync(a, template, "b210.xlsx", slots);
        var source = await SourceAsync(a, SourceFixtures.Pdf("Typed Person audit report with enough text"), "fs.pdf");
        while (await Worker.RunOneAsync(CancellationToken.None)) { }

        var started = await (await a.PostAsJsonAsync("/api/runs", new { recipeId, sourceFileIds = new[] { source } })).Content.ReadFromJsonAsync<JsonObject>();
        while (await Worker.RunOneAsync(CancellationToken.None)) { }

        var run = await a.GetFromJsonAsync<JsonObject>($"/api/runs/{started!["runId"]}");
        var rows = run!["values"]![0]!["value"]!["rows"]!.AsObject();
        Assert.Equal(["3", "4"], rows.Select(r => r.Key));
    }
}
