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
    public async Task Uploading_a_template_is_enough_to_get_a_recipe()
    {
        var client = await app.SignedInAsync("a@example.com");
        var template = WordFixtures.Docx(WordFixtures.P("GIẤY MỜI"), WordFixtures.P("Kính mời ông Nguyễn Văn A đến Sở."));
        var key = await UploadAsync(client, template, "Giấy mời.docx");

        var started = await (await client.PostAsJsonAsync("/api/templates", new { key, name = "Giấy mời.docx", createRecipe = true })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(await app.Services.GetRequiredService<JobWorker>().RunOneAsync(CancellationToken.None));

        var job = await client.GetFromJsonAsync<JsonObject>($"/api/jobs/{started!["jobId"]}");
        var recipeId = job!["result"]!["recipeId"]!.GetValue<Guid>();
        var recipe = await client.GetFromJsonAsync<JsonObject>($"/api/recipes/{recipeId}");
        Assert.Equal("Giấy mời", recipe!["name"]!.GetValue<string>());
        Assert.Equal("Khi cần mời công dân đến làm việc", recipe["useWhen"]!.GetValue<string>());
        Assert.Equal("Họ tên", recipe["version"]!["slots"]![0]!["label"]!.GetValue<string>());
    }

    static byte[] Invitation(string when) => WordFixtures.Docx(
        WordFixtures.P("GIẤY MỜI"),
        WordFixtures.P("Kính mời ông Nguyễn Văn A đến Sở."),
        WordFixtures.P($"Thời gian: {when}, tại phòng họp số 2, số 6 Alexandre de Rhodes, Quận 1."),
        WordFixtures.P("Đề nghị ông mang theo hộ chiếu và các giấy tờ liên quan khi đến làm việc."));

    async Task<JsonObject> AnalyseAsync(HttpClient client, object body)
    {
        var started = await (await client.PostAsJsonAsync("/api/templates", body)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(await app.Services.GetRequiredService<JobWorker>().RunOneAsync(CancellationToken.None));
        var job = await client.GetFromJsonAsync<JsonObject>($"/api/jobs/{started!["jobId"]}");
        Assert.Equal("done", job!["status"]!.GetValue<string>());
        return job["result"]!.AsObject();
    }

    [Fact]
    public async Task A_template_of_a_form_already_saved_is_flagged_before_the_ai_and_added_only_when_asked()
    {
        var client = await app.SignedInAsync("a@example.com");
        var first = await AnalyseAsync(client, new { key = await UploadAsync(client, Invitation("8 giờ ngày 20/10/2026"), "Giấy mời.docx"), name = "Giấy mời.docx", createRecipe = true });
        var calls = ai.Requests.Count;

        // A filled copy of the same form (another date): not analysed, the existing recipe is named.
        var copy = await AnalyseAsync(client, new { key = await UploadAsync(client, Invitation("9 giờ ngày 25/11/2026"), "Giấy mời - 2026-11-25.docx"), name = "Giấy mời - 2026-11-25.docx", createRecipe = true });
        Assert.Equal(first["recipeId"]!.GetValue<Guid>(), copy["duplicate"]!["recipeId"]!.GetValue<Guid>());
        Assert.Equal("Giấy mời", copy["duplicate"]!["name"]!.GetValue<string>());
        Assert.InRange(copy["duplicate"]!["similarity"]!.GetValue<double>(), 0.6, 1);
        Assert.Null(copy["recipeId"]);
        Assert.Equal(calls, ai.Requests.Count);
        Assert.Single(await client.GetFromJsonAsync<JsonArray>("/api/recipes") ?? []);

        // "Vẫn thêm": the same uploaded file, analysed and saved as a second recipe.
        var forced = await AnalyseAsync(client, new { fileId = copy["templateFileId"], createRecipe = true, allowDuplicate = true });
        Assert.NotNull(forced["recipeId"]);
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonArray>("/api/recipes"))!.Count);
    }

    [Fact]
    public void Copies_of_one_form_score_high_and_different_forms_with_the_same_letterhead_score_low()
    {
        const string head = "CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM\nĐộc lập - Tự do - Hạnh phúc\nSỞ NGOẠI VỤ THÀNH PHỐ HỒ CHÍ MINH\n";
        var invite = head + "GIẤY MỜI\nKính mời ông Nguyễn Văn A đến Sở vào 8 giờ ngày 20/10/2026 để trao đổi về hồ sơ xin cấp thị thực.";
        var inviteCopy = head + "GIẤY MỜI\nKính mời bà Trần Thị B đến Sở vào 9 giờ ngày 25/11/2026 để trao đổi về hồ sơ xin cấp thị thực.";
        var note = head + "CÔNG HÀM\nSở Ngoại vụ trân trọng thông báo Tổng Lãnh sự quán về việc công dân bị tạm giữ và đề nghị phối hợp.";

        Assert.InRange(Documents.Templates.TemplateSimilarity.Score(invite, inviteCopy), 0.6, 1);
        Assert.InRange(Documents.Templates.TemplateSimilarity.Score(invite, note), 0, 0.5);
        Assert.Equal(1, Documents.Templates.TemplateSimilarity.Score(invite, invite.ToUpperInvariant()));
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
