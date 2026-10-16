using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Documents.Ingest;
using AutoFill.Documents.Templates;
using static AutoFill.Documents.Templates.SchemaBuilder;

namespace AutoFill.Documents.Runs;

/// <summary>
/// The AI part of a run. Slots are sent in batches (same group together, so related values stay
/// consistent); a row rule is sent a few dozen rows at a time with all its columns at once (a
/// question's comment, Y/N and impact, or a field's Dutch and French, are answered together).
/// The sources come first in every prompt, so the provider's prompt cache serves later batches.
/// </summary>
public sealed class AiFiller(IAiClient ai, RunOptions options)
{
    const string Instructions = """
        Bạn điền các ô của một file mẫu bằng thông tin trong bộ tài liệu nguồn.
        Theo cách lấy của từng ô:
        - extract: lấy từ nguồn. Số, mã, ngày, tên riêng chép đúng như nguồn (chỉ đổi định dạng khi hướng dẫn yêu cầu), không làm tròn, không dịch số.
        - generate: viết theo nội dung nguồn, theo hướng dẫn và theo văn phong của giá trị mẫu cũ. Không thêm thông tin không có trong nguồn.
        - translate: dịch văn bản được chỉ định sang ngôn ngữ được yêu cầu; giữ nguyên số, mã, tên riêng; các bản dịch của cùng một văn bản phải cùng nghĩa và cùng số câu.
        Quy tắc chung:
        - Không đoán. Nguồn không có thông tin thì status "not_found" và value "".
        - status "found": thông tin có sẵn trong nguồn; "derived": phải suy ra, tổng hợp hoặc dịch.
        - Giữ cách viết của giá trị mẫu cũ (IN HOA hay thường, định dạng ngày, dấu câu) nếu có.
        - Ô kiểu boolean/enum: value chỉ là một trong các giá trị cho phép.
        - Ngày viết dd/MM/yyyy và số viết bằng chữ số với dấu chấm thập phân, trừ khi hướng dẫn hoặc giá trị mẫu cũ khác.
        - evidence: các đoạn trích NGUYÊN VĂN, ngắn (dưới 200 ký tự) từ nguồn làm căn cứ, kèm số tài liệu (doc) và số trang (page) theo dòng "--- Trang N ---". Rỗng khi not_found hoặc khi dịch.
        - confidence: 0..1, mức chắc chắn giá trị đúng.
        - Ô theo dòng: trả lời trong rows, mỗi dòng được hỏi một phần tử {row, value}; value của ô để "".
        Trả lời đủ mọi ô được hỏi.
        """;

    sealed record Item(List<Slot> Slots, IReadOnlyList<int>? Rows, string? Translate);

    sealed record Answer(string SlotId, string Value, List<RowAnswer> Rows, string Status, List<EvidenceAnswer> Evidence, double Confidence, string Note);
    sealed record RowAnswer(int Row, string Value);
    sealed record EvidenceAnswer(int Doc, int Page, string Quote);
    sealed record Reply(List<Answer> Values);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The sources block shared by every prompt of a run; also says whether text was cut.</summary>
    public (string Text, bool Truncated) Sources(IReadOnlyList<RunSource> sources)
    {
        var sb = new StringBuilder("# Tài liệu nguồn\n");
        var left = options.MaxSourceChars;
        var truncated = false;
        for (var i = 0; i < sources.Count; i++)
        {
            sb.Append($"\n## Tài liệu {i + 1}: {sources[i].Name}\n");
            if (left <= 0) { sb.Append("(bỏ qua: vượt giới hạn độ dài)\n"); truncated = true; continue; }
            var (text, cut) = SourceText.Render(sources[i].Document, left);
            sb.Append(text);
            left -= text.Length;
            truncated |= cut;
        }
        return (sb.ToString(), truncated);
    }

    /// <param name="slots">AI slots of one phase</param>
    /// <param name="known">values already found (the text a "translate from slot" item translates)</param>
    /// <param name="onBatch">(batches done, batches total)</param>
    public async Task<Dictionary<string, SlotResult>> FillAsync(
        IReadOnlyList<Slot> slots, string sourcesBlock, RunInput input, IReadOnlyDictionary<string, SlotResult> known,
        Func<int, int, Task>? onBatch, CancellationToken ct)
    {
        var items = Plan(slots, known);
        var results = new Dictionary<string, SlotResult>();
        var done = 0;
        using var gate = new SemaphoreSlim(options.Parallelism);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var answers = await AskAsync(item, sourcesBlock, input, ct);
                lock (results)
                {
                    foreach (var slot in item.Slots) Merge(results, slot, answers.FirstOrDefault(a => a.SlotId == slot.Id), item.Rows);
                    done++;
                }
                if (onBatch is not null) await onBatch(done, items.Count);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    List<Item> Plan(IReadOnlyList<Slot> slots, IReadOnlyDictionary<string, SlotResult> known)
    {
        var items = new List<Item>();
        // A translation of another slot's value travels alone with that text.
        foreach (var slot in slots.Where(s => RuleStrategies.Config(s, "fromSlot") is not null && !IsRows(s)))
            items.Add(new Item([slot], null, known.GetValueOrDefault(RuleStrategies.Config(slot, "fromSlot")!)?.Value.Text ?? ""));

        var plain = slots.Where(s => !IsRows(s) && RuleStrategies.Config(s, "fromSlot") is null);
        foreach (var group in plain.GroupBy(s => s.Group))
            foreach (var batch in group.Chunk(options.BatchSize)) items.Add(new Item([.. batch], null, null));

        // Columns of the same rule (same group, same rows) go together, a few dozen rows per call.
        foreach (var rule in slots.Where(IsRows).GroupBy(s => (s.Group, Rows: string.Join(",", Rows(s)))))
            foreach (var rows in Rows(rule.First()).Chunk(options.RowsPerCall)) items.Add(new Item([.. rule], rows, null));
        return items;
    }

    async Task<List<Answer>> AskAsync(Item item, string sourcesBlock, RunInput input, CancellationToken ct)
    {
        var prompt = new StringBuilder(sourcesBlock);
        if (!string.IsNullOrWhiteSpace(input.Notes)) prompt.Append($"\n# Ghi chú của công thức\n{input.Notes.Trim()}\n");
        prompt.Append("\n# Các ô cần điền\n");
        foreach (var slot in item.Slots) prompt.Append(Describe(slot)).Append('\n');
        if (item.Translate is not null) prompt.Append($"\nVăn bản cần dịch:\n{item.Translate}\n");
        if (item.Rows is not null)
        {
            var labels = input.RowLabels.GetValueOrDefault(item.Slots[0].Id) ?? new Dictionary<int, string>();
            prompt.Append("\nCác dòng cần trả lời (số dòng: nhãn của dòng). Ô dịch thì dịch nhãn của dòng:\n");
            foreach (var row in item.Rows) prompt.Append($"{row}: {labels.GetValueOrDefault(row, "")}\n");
        }

        var schema = Obj(("values", Arr(Obj(
            ("slotId", Enum(item.Slots.Select(s => s.Id))),
            ("value", Str()),
            ("rows", Arr(Obj(("row", Int()), ("value", Str())))),
            ("status", Enum(new[] { "found", "derived", "not_found" })),
            ("evidence", Arr(Obj(("doc", Int()), ("page", Int()), ("quote", Str())))),
            ("confidence", Num()),
            ("note", Str())))));
        var node = await ai.GenerateJsonAsync(new AiRequest("run-fill", Instructions, prompt.ToString(), "slot_values", schema), ct);
        return node.Deserialize<Reply>(Json)?.Values ?? [];
    }

    static string Describe(Slot slot)
    {
        var sb = new StringBuilder($"- {slot.Id} | {slot.Label} | kiểu {slot.ValueType.ToString().ToLowerInvariant()} | cách lấy {slot.Strategy.ToString().ToLowerInvariant()}");
        if (slot.Locations.OfType<RowsLocation>().FirstOrDefault() is { } rows) sb.Append($" | theo dòng, cột {rows.Column}");
        if (slot.Strategy == SlotStrategy.Translate) sb.Append($" | dịch sang {RuleStrategies.Config(slot, "language") ?? "(xem hướng dẫn)"}");
        if (slot.Options.Count > 0) sb.Append($" | chỉ được chọn: {string.Join(" / ", slot.Options)}");
        if (!string.IsNullOrWhiteSpace(slot.Instructions)) sb.Append($" | hướng dẫn: {slot.Instructions.Trim()}");
        if (!string.IsNullOrWhiteSpace(slot.SampleValue) && slot.ValueType != SlotValueType.LongText) sb.Append($" | giá trị mẫu cũ: \"{slot.SampleValue}\"");
        else if (!string.IsNullOrWhiteSpace(slot.SampleValue)) sb.Append($"\n  Văn bản mẫu cũ (văn phong cần theo):\n  {slot.SampleValue.Replace("\n", "\n  ")}");
        return sb.ToString();
    }

    /// <summary>Adds one answer to the results; row chunks of the same slot are combined.</summary>
    static void Merge(Dictionary<string, SlotResult> results, Slot slot, Answer? answer, IReadOnlyList<int>? rows)
    {
        var warnings = new List<string>();
        if (answer is null) warnings.Add("AI không trả lời ô này");
        var evidence = (answer?.Evidence ?? []).Where(e => e.Quote.Trim().Length > 0).Select(e => new Evidence(e.Doc - 1, e.Page, e.Quote.Trim(), false)).ToList();
        var notFound = answer?.Status == "not_found";
        string? note = string.IsNullOrWhiteSpace(answer?.Note) ? null : answer.Note.Trim();

        if (rows is null)
        {
            results[slot.Id] = new SlotResult(new SlotValue(notFound ? "" : answer?.Value ?? ""), answer is null || notFound ? ValueOrigin.None : ValueOrigin.Ai)
            {
                Evidence = evidence, Confidence = answer?.Confidence ?? 0, Note = note, Warnings = warnings,
            };
            return;
        }
        var asked = rows.ToHashSet();
        var got = (answer?.Rows ?? []).Where(r => asked.Contains(r.Row)).GroupBy(r => r.Row).ToDictionary(g => g.Key, g => g.First().Value);
        if (answer is not null && got.Count < asked.Count) warnings.Add($"AI bỏ sót {asked.Count - got.Count} dòng");
        if (results.TryGetValue(slot.Id, out var before))
        {
            foreach (var (r, v) in before.Value.Rows ?? new Dictionary<int, string>()) got.TryAdd(r, v);
            evidence.InsertRange(0, before.Evidence);
            warnings.InsertRange(0, before.Warnings);
        }
        results[slot.Id] = new SlotResult(new SlotValue(Rows: got), got.Count == 0 ? ValueOrigin.None : ValueOrigin.Ai)
        {
            Evidence = evidence,
            Confidence = before is null ? answer?.Confidence ?? 0 : Math.Min(before.Confidence, answer?.Confidence ?? 0),
            Note = note ?? before?.Note, Warnings = warnings,
        };
    }

    static bool IsRows(Slot s) => s.Locations.Any(l => l is RowsLocation);
    static IReadOnlyList<int> Rows(Slot s) => s.Locations.OfType<RowsLocation>().First().Rows;
}
