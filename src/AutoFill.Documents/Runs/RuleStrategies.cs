using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoFill.Documents.Ingest;
using AutoFill.Documents.Templates;
using AutoFill.Documents.Templates.Excel;

namespace AutoFill.Documents.Runs;

/// <summary>
/// Values that never go through the AI: constants, lookups in a source spreadsheet, formulas over
/// other slots. Numbers, codes and Y/N answers belong here whenever a table has them.
/// </summary>
public static partial class RuleStrategies
{
    [GeneratedRegex(@"\{([A-Za-z0-9_\-]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^([A-Z]{1,3})([0-9]+)$")]
    private static partial Regex CellRef();

    /// <summary>A setting as text (numbers typed in the builder come back as text too).</summary>
    public static string? Config(Slot slot, string name) => slot.StrategyConfig?[name]?.ToString() is { Length: > 0 } v ? v : null;

    /// <summary>
    /// A rule slot nobody configured (the template analysis may pick "lookup" or "formula" but sets
    /// no table or expression) is read from the sources by the AI instead; a constant without a
    /// value keeps the template's own text.
    /// </summary>
    public static Slot Runnable(Slot slot) => slot.Strategy switch
    {
        SlotStrategy.Lookup when Config(slot, "keyColumn") is null || Config(slot, "valueColumn") is null => slot with { Strategy = SlotStrategy.Extract },
        SlotStrategy.Formula when Config(slot, "expression") is null => slot with { Strategy = SlotStrategy.Extract },
        SlotStrategy.Constant when Config(slot, "value") is null => slot.SampleValue is { Length: > 0 } sample
            ? slot with { StrategyConfig = new JsonObject { ["value"] = sample } }
            : slot with { Strategy = SlotStrategy.Extract },
        _ => slot,
    };

    /// <summary>{"value": "..."}; for a rows slot every row gets the same value.</summary>
    public static SlotResult Constant(Slot slot, IReadOnlyDictionary<int, string>? rowLabels)
    {
        var value = Config(slot, "value") ?? "";
        return new SlotResult(IsRows(slot) ? new SlotValue(Rows: (rowLabels ?? new Dictionary<int, string>()).Keys.ToDictionary(r => r, _ => value)) : new SlotValue(value), ValueOrigin.Rule);
    }

    /// <summary>
    /// {"keyColumn": "A", "valueColumn": "D", "sheet"?: "EET", "source"?: "part of the file name",
    ///  "keyFrom"?: "slot id", "key"?: "fixed key"}. A rows slot looks up each row by its label
    /// (or by the row's value of <c>keyFrom</c>). Keys match ignoring case and spacing.
    /// </summary>
    public static SlotResult Lookup(Slot slot, IReadOnlyList<RunSource> sources, IReadOnlyDictionary<int, string>? rowLabels, IReadOnlyDictionary<string, SlotResult> known)
    {
        var keyColumn = Config(slot, "keyColumn")?.ToUpperInvariant();
        var valueColumn = Config(slot, "valueColumn")?.ToUpperInvariant();
        if (keyColumn is null || valueColumn is null)
            return Fail(slot, "Chưa cấu hình cột khóa và cột giá trị cho phép tra bảng");
        var table = Table(sources, Config(slot, "source"), Config(slot, "sheet"), keyColumn, valueColumn);
        if (table.Count == 0) return Fail(slot, "Không có bảng tính nguồn nào có cột cần tra");

        var keyFrom = Config(slot, "keyFrom");
        var evidence = new List<Evidence>();
        var warnings = new List<string>();
        string? Find(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (!table.TryGetValue(Norm(key), out var hit)) { warnings.Add($"Không tra được \"{Short(key)}\" trong bảng"); return null; }
            evidence.Add(hit.Evidence);
            return hit.Value;
        }

        if (IsRows(slot))
        {
            var rows = new Dictionary<int, string>();
            foreach (var (row, label) in rowLabels ?? new Dictionary<int, string>())
            {
                var key = keyFrom is null ? label : known.GetValueOrDefault(keyFrom)?.Value.Rows?.GetValueOrDefault(row);
                if (Find(key) is { } v) rows[row] = v;
            }
            return new SlotResult(new SlotValue(Rows: rows), ValueOrigin.Rule) { Evidence = evidence, Warnings = warnings };
        }
        var single = keyFrom is null ? Config(slot, "key") : known.GetValueOrDefault(keyFrom)?.Value.Text;
        var value = Find(single);
        return new SlotResult(new SlotValue(value ?? ""), value is null ? ValueOrigin.None : ValueOrigin.Rule) { Evidence = evidence, Warnings = warnings };
    }

    /// <summary>
    /// {"expression": "{s1} * {s2}"}: arithmetic when every placeholder is a number, otherwise the
    /// placeholders are replaced as text ("{s1} - {s2}" → "Hà Nội - 2026"). Rows slots compute row by row.
    /// </summary>
    public static SlotResult Formula(Slot slot, IReadOnlyDictionary<string, SlotResult> known, IReadOnlyDictionary<int, string>? rowLabels)
    {
        var expression = Config(slot, "expression");
        if (string.IsNullOrWhiteSpace(expression)) return Fail(slot, "Chưa nhập công thức");
        var warnings = new List<string>();

        string Evaluate(Func<string, string?> valueOf)
        {
            var parts = new Dictionary<string, string>();
            foreach (Match m in Placeholder().Matches(expression))
            {
                var v = valueOf(m.Groups[1].Value);
                if (v is null) { warnings.Add($"Công thức cần \"{m.Groups[1].Value}\" nhưng ô đó chưa có giá trị"); v = ""; }
                parts[m.Value] = v;
            }
            var numbers = parts.ToDictionary(p => p.Key, p => ExcelWriter.ParseNumber(p.Value));
            if (parts.Count > 0 && numbers.Values.All(n => n is not null))
            {
                var arithmetic = Placeholder().Replace(expression, m => numbers[m.Value]!.Value.ToString("R", CultureInfo.InvariantCulture));
                try
                {
                    return Convert.ToDouble(new DataTable().Compute(arithmetic, null), CultureInfo.InvariantCulture).ToString("0.##########", CultureInfo.InvariantCulture);
                }
                catch (Exception err) when (err is EvaluateException or SyntaxErrorException or InvalidCastException or OverflowException or DivideByZeroException)
                {
                    warnings.Add("Công thức không tính được");
                    return "";
                }
            }
            return Placeholder().Replace(expression, m => parts.GetValueOrDefault(m.Value, ""));
        }

        if (IsRows(slot))
        {
            var rows = (rowLabels ?? new Dictionary<int, string>()).Keys.ToDictionary(r => r, r => Evaluate(id =>
                known.GetValueOrDefault(id)?.Value is { } v ? v.Rows?.GetValueOrDefault(r) ?? v.Text : null));
            return new SlotResult(new SlotValue(Rows: rows), ValueOrigin.Rule) { Warnings = warnings.Distinct().ToList() };
        }
        var text = Evaluate(id => known.GetValueOrDefault(id)?.Value.Text);
        return new SlotResult(new SlotValue(text), ValueOrigin.Rule) { Warnings = warnings.Distinct().ToList() };
    }

    static bool IsRows(Slot slot) => slot.Locations.Any(l => l is RowsLocation);

    static SlotResult Fail(Slot slot, string warning) =>
        new(IsRows(slot) ? new SlotValue(Rows: new Dictionary<int, string>()) : new SlotValue(""), ValueOrigin.None) { Warnings = [warning] };

    /// <summary>key → (value, evidence) from the first spreadsheet sources that have both columns.</summary>
    static Dictionary<string, (string Value, Evidence Evidence)> Table(IReadOnlyList<RunSource> sources, string? source, string? sheet, string keyColumn, string valueColumn)
    {
        var table = new Dictionary<string, (string, Evidence)>();
        for (var s = 0; s < sources.Count; s++)
        {
            var src = sources[s];
            if (src.Document.Kind is not (SourceKind.Xlsx or SourceKind.Xls)) continue;
            if (source is not null && !src.Name.Contains(source, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var page in src.Document.Pages.Where(p => sheet is null || string.Equals(p.Label, sheet, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var row in page.Blocks.SelectMany(b => b.Rows ?? []))
                {
                    var byColumn = row.Where(c => c.Ref is not null && CellRef().IsMatch(c.Ref))
                        .ToDictionary(c => CellRef().Match(c.Ref!).Groups[1].Value, c => c);
                    if (byColumn.TryGetValue(keyColumn, out var key) && byColumn.TryGetValue(valueColumn, out var value) && key.Text.Length > 0)
                        table.TryAdd(Norm(key.Text), (value.Text, new Evidence(s, page.Number, $"{key.Ref}: {key.Text} | {value.Ref}: {value.Text}", true)));
                }
            }
        }
        return table;
    }

    public static string Norm(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    static string Short(string text) => text.Length > 50 ? text[..50] + "…" : text;
}
