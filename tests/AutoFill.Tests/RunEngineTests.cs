using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Documents.Ingest;
using AutoFill.Documents.Runs;
using AutoFill.Documents.Templates;

namespace AutoFill.Tests;

public sealed class RunEngineTests
{
    static readonly Guid FileId = Guid.NewGuid();

    /// <summary>An incoming letter: page 1 names the person, page 2 the appointment.</summary>
    static RunSource Letter() => new(FileId, "van-ban-den.pdf", new SourceDocument(SourceKind.Pdf, [
        new SourcePage(1, [SourceBlock.Paragraph("Họ tên: TRẦN THỊ B, hộ chiếu P7654321."), SourceBlock.Paragraph("Quốc tịch: Australia")]),
        new SourcePage(2, [SourceBlock.Paragraph("Đề nghị gặp lúc 14 giờ ngày 24/9/2026 tại trại giam."), SourceBlock.Paragraph("Tỷ lệ phí 0.05")]),
    ]));

    /// <summary>An EET-like sheet: ISIN | field name | value.</summary>
    static RunSource Eet() => new(Guid.NewGuid(), "EET.xlsx", new SourceDocument(SourceKind.Xlsx, [
        new SourcePage(1, [SourceBlock.Table([
            [new SourceCell("Field", "A1"), new SourceCell("Value", "B1")],
            [new SourceCell("Minimum  sustainable share", "A2"), new SourceCell("0.2", "B2")],
            [new SourceCell("Taxonomy aligned", "A3"), new SourceCell("Y", "B3")],
        ])], "EET"),
    ]));

    static Slot S(string id, SlotStrategy strategy = SlotStrategy.Extract, SlotValueType type = SlotValueType.Text, string group = "",
        JsonObject? config = null, IReadOnlyList<string>? options = null, SlotLocation? at = null) =>
        new() { Id = id, Label = $"Ô {id}", Strategy = strategy, ValueType = type, Group = group, StrategyConfig = config, Options = options ?? [], Locations = [at ?? new CellLocation("S", "A1")] };

    static RunInput Input(IReadOnlyList<Slot> slots, IReadOnlyList<RunSource>? sources = null, IReadOnlyList<RecipeRule>? rules = null,
        Dictionary<string, IReadOnlyDictionary<int, string>>? rowLabels = null, bool checkMissing = false) =>
        new(slots, sources ?? [Letter()], rowLabels ?? [], rules ?? [], "Ghi chú công thức", checkMissing);

    static string[] AskedIds(AiRequest r) => r.Schema["properties"]!["values"]!["items"]!["properties"]!["slotId"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    static JsonObject Value(string id, string value, string quote = "", int page = 1, double confidence = 0.9, string status = "found", JsonArray? rows = null) => new()
    {
        ["slotId"] = id, ["value"] = value, ["rows"] = rows ?? [], ["status"] = status, ["confidence"] = confidence, ["note"] = "",
        ["evidence"] = quote.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["doc"] = 1, ["page"] = page, ["quote"] = quote }),
    };

    static JsonObject Reply(params JsonObject[] values) => new() { ["values"] = new JsonArray(values) };

    [Fact]
    public async Task Constants_lookups_and_formulas_need_no_ai()
    {
        var ai = new FakeAi(_ => throw new InvalidOperationException("no AI expected"));
        var rows = new RowsLocation("T", "A", "C", [5, 6, 7]);
        Slot[] slots =
        [
            S("office", SlotStrategy.Constant, config: new JsonObject { ["value"] = "Sở Ngoại vụ" }),
            S("share", SlotStrategy.Lookup, at: rows, config: new JsonObject { ["sheet"] = "EET", ["keyColumn"] = "a", ["valueColumn"] = "B" }),
            S("double", SlotStrategy.Formula, SlotValueType.Number, config: new JsonObject { ["expression"] = "{x} * 2" }),
            S("x", SlotStrategy.Constant, config: new JsonObject { ["value"] = "0.125" }),
            S("title", SlotStrategy.Formula, config: new JsonObject { ["expression"] = "{office} - {x}" }),
        ];
        var labels = new Dictionary<string, IReadOnlyDictionary<int, string>>
        {
            ["share"] = new Dictionary<int, string> { [5] = "Minimum sustainable share", [6] = "taxonomy ALIGNED", [7] = "Not in EET" },
        };

        var result = await new RunEngine(ai, new RunOptions()).RunAsync(Input(slots, [Letter(), Eet()], rowLabels: labels));

        Assert.Equal("Sở Ngoại vụ", result.Values["office"].Value.Text);
        Assert.Equal(new Dictionary<int, string> { [5] = "0.2", [6] = "Y" }, result.Values["share"].Value.Rows);
        Assert.Contains(result.Values["share"].Warnings, w => w.Contains("Not in EET"));
        Assert.Equal((1, 1), (result.Values["share"].Evidence[0].Source, result.Values["share"].Evidence[0].Page));
        Assert.Equal("0.25", result.Values["double"].Value.Text);
        Assert.Equal("Sở Ngoại vụ - 0.125", result.Values["title"].Value.Text);
        Assert.Empty(ai.Requests);
    }

    [Fact]
    public async Task Slots_go_in_batches_by_group_and_row_rules_by_chunks_with_all_their_columns()
    {
        var running = 0;
        var maxRunning = 0;
        var ai = new FakeAi(r =>
        {
            var now = Interlocked.Increment(ref running);
            lock (r) maxRunning = Math.Max(maxRunning, now);
            Thread.Sleep(30);
            Interlocked.Decrement(ref running);
            var asked = r.Input.Split('\n').SkipWhile(l => !l.StartsWith("Các dòng")).Skip(1).TakeWhile(l => l.Contains(':'))
                .Select(l => int.Parse(l[..l.IndexOf(':')])).ToList();
            return Reply([.. AskedIds(r).Select(id => Value(id, "", rows: new JsonArray([.. asked.Select(n => (JsonNode)new JsonObject { ["row"] = n, ["value"] = "Y" })])))]);
        });
        var rows = Enumerable.Range(10, 95).ToList();
        Slot[] slots =
        [
            .. Enumerable.Range(1, 15).Select(i => S($"a{i}", group: "A")),
            .. Enumerable.Range(1, 8).Select(i => S($"b{i}", group: "B")),
            S("comment", SlotStrategy.Generate, group: "Q", at: new RowsLocation("T", "B", "G", rows)),
            S("yn", SlotStrategy.Extract, SlotValueType.Boolean, "Q", options: ["Y", "N"], at: new RowsLocation("T", "B", "I", rows)),
        ];
        var labels = new Dictionary<string, IReadOnlyDictionary<int, string>>
        {
            ["comment"] = rows.ToDictionary(r => r, r => $"Câu hỏi {r}"), ["yn"] = rows.ToDictionary(r => r, r => $"Câu hỏi {r}"),
        };

        var result = await new RunEngine(ai, new RunOptions(Parallelism: 2)).RunAsync(Input(slots, rowLabels: labels));

        var batches = ai.Requests.Select(AskedIds).Select(ids => ids.Length).OrderBy(n => n).ToList();
        Assert.Equal([2, 2, 2, 5, 8, 10], batches); // A: 10 + 5, B: 8, rows: 40 + 40 + 15 rows, both columns each time
        Assert.True(maxRunning <= 2);
        Assert.Equal(95, result.Values["yn"].Value.Rows!.Count);
        Assert.DoesNotContain(result.Values["yn"].Warnings, w => w.Contains("bỏ sót"));
        // The sources come first in every prompt (prompt cache).
        Assert.All(ai.Requests, r => Assert.StartsWith("# Tài liệu nguồn", r.Input));
    }

    [Fact]
    public async Task Values_are_checked_against_the_sources_by_code()
    {
        var ai = new FakeAi(r => Reply([.. AskedIds(r).Select(id => id switch
        {
            "name" => Value(id, "TRẦN THỊ B", "Họ tên: TRẦN  THỊ B", page: 2), // right quote, wrong page: corrected
            "passport" => Value(id, "P7654329", "hộ chiếu P7654329"),          // a digit the source does not have
            "date" => Value(id, "24/13/2026", "ngày 24/9/2026", page: 2),        // not a date
            "yn" => Value(id, "Có", "Quốc tịch: Australia"),                     // not an allowed option
            "rate" => Value(id, "0.05", "Tỷ lệ phí 0.05", page: 2),             // a code-like number stays as written
            "unsure" => Value(id, "Úc", "Quốc tịch: Australia", confidence: 0.3, status: "derived"),
            "absent" => Value(id, "", status: "not_found"),
            _ => Value(id, "bịa", "câu không có trong nguồn"),
        })]));
        Slot[] slots =
        [
            S("name"), S("passport"), S("date", type: SlotValueType.Date), S("yn", type: SlotValueType.Boolean, options: ["Y", "N"]),
            S("rate"), S("unsure", SlotStrategy.Generate), S("absent"), S("made-up"),
        ];

        var v = (await new RunEngine(ai, new RunOptions()).RunAsync(Input(slots))).Values;

        Assert.Empty(v["name"].Warnings);
        Assert.Equal((0, 1, true), (v["name"].Evidence[0].Source, v["name"].Evidence[0].Page, v["name"].Evidence[0].Found));
        Assert.Contains(v["passport"].Warnings, w => w.Contains("7654329"));
        Assert.Contains(v["date"].Warnings, w => w.Contains("không phải ngày"));
        Assert.Contains(v["yn"].Warnings, w => w.Contains("không thuộc"));
        Assert.Equal(("0.05", 0), (v["rate"].Value.Text, v["rate"].Warnings.Count));
        Assert.Contains(v["unsure"].Warnings, w => w.Contains("không chắc chắn"));
        Assert.Equal((ValueOrigin.None, "Không tìm thấy thông tin trong nguồn"), (v["absent"].Origin, v["absent"].Warnings.Single()));
        Assert.Contains(v["made-up"].Warnings, w => w.Contains("Không có căn cứ"));
    }

    [Fact]
    public async Task Translations_of_a_value_come_after_it_and_recipe_rules_run_last()
    {
        var ai = new FakeAi(r => Reply([.. AskedIds(r).Select(id => id switch
        {
            "summary" => Value(id, "Gặp ngày 24/9/2026. Tại trại giam.", "ngày 24/9/2026", page: 2, status: "derived"),
            "summary-en" => Value(id, "Meeting on 24/9/2026.", status: "derived"),
            "received" => Value(id, "24/9/2026", "24/9/2026", page: 2),
            _ => Value(id, "14/9/2026", "24/9/2026", page: 2),
        })]));
        Slot[] slots =
        [
            S("summary", SlotStrategy.Generate),
            S("summary-en", SlotStrategy.Translate, config: new JsonObject { ["fromSlot"] = "summary", ["language"] = "tiếng Anh" }),
            S("received", type: SlotValueType.Date), S("meeting", type: SlotValueType.Date),
        ];
        RecipeRule[] rules = [new("dateAfter", "meeting", "received"), new("sameSentenceCount", "summary-en", "summary")];

        var v = (await new RunEngine(ai, new RunOptions()).RunAsync(Input(slots, rules: rules))).Values;

        var translate = ai.Requests.Single(r => AskedIds(r).Contains("summary-en"));
        Assert.Contains("Văn bản cần dịch:\nGặp ngày 24/9/2026. Tại trại giam.", translate.Input);
        Assert.Contains("Ngày phải sau", v["meeting"].Warnings.Single());
        Assert.Contains("Số câu khác", v["summary-en"].Warnings.Single());
        Assert.Empty(v["received"].Warnings);
    }

    [Fact]
    public async Task Missing_information_is_kept_only_when_its_quote_is_in_the_sources()
    {
        var ai = new FakeAi(r => r.Purpose == "run-missing"
            ? new JsonObject
            {
                ["items"] = new JsonArray(
                    new JsonObject { ["item"] = "Giờ hẹn gặp", ["doc"] = 1, ["page"] = 2, ["quote"] = "lúc 14 giờ ngày 24/9/2026" },
                    new JsonObject { ["item"] = "Bịa ra", ["doc"] = 1, ["page"] = 1, ["quote"] = "không hề có câu này" }),
            }
            : Reply([.. AskedIds(r).Select(id => Value(id, "TRẦN THỊ B", "TRẦN THỊ B"))]));

        var result = await new RunEngine(ai, new RunOptions()).RunAsync(Input([S("name")], checkMissing: true));

        Assert.Equal(new MissingItem("Giờ hẹn gặp", 0, 2, "lúc 14 giờ ngày 24/9/2026"), result.Missing.Single());
        Assert.Contains("TRẦN THỊ B", ai.Requests.Single(r => r.Purpose == "run-missing").Input);
    }
}
