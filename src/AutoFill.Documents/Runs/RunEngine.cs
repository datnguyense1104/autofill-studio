using System.Text;
using System.Text.Json;
using AutoFill.Core;
using AutoFill.Documents.Templates;
using static AutoFill.Documents.Templates.SchemaBuilder;

namespace AutoFill.Documents.Runs;

/// <summary>
/// One run: sources + a recipe's slots → a value for every slot, with evidence and warnings.
/// Rules first (constants, lookups), then the AI, then translations of AI values and formulas,
/// then checks by code, then one AI pass asking what important information is still missing.
/// </summary>
public sealed class RunEngine(IAiClient ai, RunOptions options)
{
    const string MissingInstructions = """
        Bạn kiểm tra một văn bản vừa được điền từ bộ tài liệu nguồn.
        Liệt kê các thông tin QUAN TRỌNG trong nguồn mà các giá trị đã điền chưa có: tên người, số giấy tờ, ngày, địa điểm, số tiền, yêu cầu, thời hạn, điều kiện...
        Không liệt kê thông tin đã có (kể cả khi viết khác đi), thông tin chung chung, hay chữ của mẫu.
        Mỗi mục: item = thông tin còn thiếu (ngắn gọn), doc và page = vị trí trong nguồn, quote = trích nguyên văn ngắn.
        Không thiếu gì thì trả về danh sách rỗng.
        """;

    sealed record MissingReply(List<MissingAnswer> Items);
    sealed record MissingAnswer(string Item, int Doc, int Page, string Quote);

    /// <param name="onProgress">(step, done, total) for the job's progress line</param>
    public async Task<RunResult> RunAsync(RunInput input, Func<string, int, int, Task>? onProgress = null, CancellationToken ct = default)
    {
        var results = new Dictionary<string, SlotResult>();
        var notes = new List<string>();
        var filler = new AiFiller(ai, options);
        var (sources, truncated) = filler.Sources(input.Sources);
        if (truncated) notes.Add($"Tài liệu nguồn dài hơn {options.MaxSourceChars:N0} ký tự: phần sau bị bỏ qua khi điền, hãy kiểm tra kỹ");

        IReadOnlyDictionary<int, string>? Labels(Slot s) => input.RowLabels.GetValueOrDefault(s.Id);
        bool Is(Slot s, SlotStrategy strategy) => s.Strategy == strategy;
        bool TranslatesSlot(Slot s) => Is(s, SlotStrategy.Translate) && RuleStrategies.Config(s, "fromSlot") is not null;
        bool LookupAfterAi(Slot s) => RuleStrategies.Config(s, "keyFrom") is { } from && input.Slots.Any(o => o.Id == from && IsAi(o));
        bool IsAi(Slot s) => s.Strategy is SlotStrategy.Extract or SlotStrategy.Generate or SlotStrategy.Translate;

        foreach (var s in input.Slots.Where(s => Is(s, SlotStrategy.Constant))) results[s.Id] = RuleStrategies.Constant(s, Labels(s));
        foreach (var s in input.Slots.Where(s => Is(s, SlotStrategy.Lookup) && !LookupAfterAi(s))) results[s.Id] = RuleStrategies.Lookup(s, input.Sources, Labels(s), results);

        var phaseA = input.Slots.Where(s => IsAi(s) && !TranslatesSlot(s)).ToList();
        if (phaseA.Count > 0)
        {
            if (onProgress is not null) await onProgress("AI điền các ô", 0, 1);
            foreach (var (id, r) in await filler.FillAsync(phaseA, sources, input, results,
                onProgress is null ? null : (d, t) => onProgress("AI điền các ô", d, t), ct)) results[id] = r;
        }

        foreach (var s in input.Slots.Where(s => Is(s, SlotStrategy.Lookup) && LookupAfterAi(s))) results[s.Id] = RuleStrategies.Lookup(s, input.Sources, Labels(s), results);
        var phaseB = input.Slots.Where(TranslatesSlot).ToList();
        if (phaseB.Count > 0)
        {
            if (onProgress is not null) await onProgress("AI dịch", 0, 1);
            foreach (var (id, r) in await filler.FillAsync(phaseB, sources, input, results, null, ct)) results[id] = r;
        }

        // Formulas may use other formulas: evaluate those whose inputs are ready, until none is left.
        var formulas = input.Slots.Where(s => Is(s, SlotStrategy.Formula)).ToList();
        while (formulas.Count > 0)
        {
            var ready = formulas.Where(f => Inputs(f).All(id => results.ContainsKey(id) || input.Slots.All(o => o.Id != id))).ToList();
            if (ready.Count == 0) ready = formulas; // a cycle: evaluate anyway, missing inputs become warnings
            foreach (var f in ready) results[f.Id] = RuleStrategies.Formula(f, results, Labels(f));
            formulas = formulas.Except(ready).ToList();
        }

        var verifier = new Verifier(input.Sources, options.ReviewBelow);
        foreach (var slot in input.Slots)
            results[slot.Id] = verifier.Check(slot, results.GetValueOrDefault(slot.Id) ?? new SlotResult(new SlotValue(""), ValueOrigin.None));
        Verifier.ApplyRules(input.Rules, results);

        IReadOnlyList<MissingItem> missing = [];
        if (input.CheckMissing && input.Sources.Count > 0 && input.Slots.Any(s => IsAi(s) && !s.Locations.Any(l => l is RowsLocation)))
        {
            if (onProgress is not null) await onProgress("AI rà thông tin còn thiếu", 0, 1);
            missing = await MissingAsync(input, results, sources, verifier, ct);
        }
        return new RunResult(results, missing) { Notes = notes };
    }

    /// <summary>
    /// The reviewer asks for one slot to be written again ("bổ sung ngày hẹn", "ngắn hơn"): one AI
    /// call with the current value and the instruction, then the same checks as a run.
    /// </summary>
    public async Task<SlotResult> ReviseAsync(Slot slot, RunInput input, SlotValue current, string instruction, CancellationToken ct = default)
    {
        var shown = current.Rows is { } rows ? string.Join("\n", rows.OrderBy(r => r.Key).Select(r => $"{r.Key}: {r.Value}")) : current.Text ?? "";
        var revised = slot with
        {
            // A rule-based slot rewritten by hand becomes an AI one for this call.
            Strategy = slot.Strategy is SlotStrategy.Extract or SlotStrategy.Generate or SlotStrategy.Translate ? slot.Strategy : SlotStrategy.Generate,
            StrategyConfig = slot.Strategy == SlotStrategy.Translate ? slot.StrategyConfig : null,
            Instructions = $"{slot.Instructions}\nGiá trị hiện tại:\n{shown}\nNgười duyệt yêu cầu sửa: {instruction}".Trim(),
        };
        var filler = new AiFiller(ai, options);
        var (sources, _) = filler.Sources(input.Sources);
        var results = await filler.FillAsync([revised], sources, input, new Dictionary<string, SlotResult>(), null, ct);
        return new Verifier(input.Sources, options.ReviewBelow).Check(slot, results[slot.Id]);
    }

    static IEnumerable<string> Inputs(Slot formula) =>
        System.Text.RegularExpressions.Regex.Matches(RuleStrategies.Config(formula, "expression") ?? "", @"\{([A-Za-z0-9_\-]+)\}").Select(m => m.Groups[1].Value);

    async Task<IReadOnlyList<MissingItem>> MissingAsync(RunInput input, Dictionary<string, SlotResult> results, string sources, Verifier verifier, CancellationToken ct)
    {
        var prompt = new StringBuilder(sources).Append("\n# Các giá trị đã điền\n");
        foreach (var slot in input.Slots.Where(s => !s.Locations.Any(l => l is RowsLocation)))
            prompt.Append($"- {slot.Label}: {results[slot.Id].Value.Text}\n");
        var schema = Obj(("items", Arr(Obj(("item", Str()), ("doc", Int()), ("page", Int()), ("quote", Str())))));
        var node = await ai.GenerateJsonAsync(new AiRequest("run-missing", MissingInstructions, prompt.ToString(), "missing_info", schema), ct);
        var reply = node.Deserialize<MissingReply>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        // Only items whose quote is really in the sources: an invented "missing" item is noise.
        var probe = new Slot { Id = "_", Label = "_", Locations = [], Required = false };
        return (reply?.Items ?? [])
            .Select(i => (i, e: verifier.Check(probe, new SlotResult(new SlotValue(""), ValueOrigin.None) { Evidence = [new Evidence(i.Doc - 1, i.Page, i.Quote, false)] }).Evidence[0]))
            .Where(x => x.e.Found && x.i.Item.Trim().Length > 0)
            .Select(x => new MissingItem(x.i.Item.Trim(), x.e.Source, x.e.Page, x.e.Quote))
            .ToList();
    }
}
