using System.Globalization;
using System.Text.RegularExpressions;
using AutoFill.Documents.Ingest;
using AutoFill.Documents.Templates;
using AutoFill.Documents.Templates.Excel;

namespace AutoFill.Documents.Runs;

/// <summary>
/// Checks every value by code after filling, and turns doubts into warnings the reviewer sees:
/// the type is right, the quote is really in the source, every number came from the source,
/// the AI was sure enough, and the recipe's own rules hold.
/// </summary>
public sealed partial class Verifier
{
    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"[.!?…]+(\s|$)")]
    private static partial Regex SentenceEnd();

    public static readonly string[] DateFormats = ["d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "d-M-yyyy", "d.M.yyyy"];

    readonly IReadOnlyList<RunSource> sources;
    readonly List<List<string>> pages; // normalized text per source, per page
    readonly HashSet<string> sourceNumbers;
    readonly double reviewBelow;

    public Verifier(IReadOnlyList<RunSource> sources, double reviewBelow)
    {
        this.sources = sources;
        this.reviewBelow = reviewBelow;
        pages = sources.Select(s => s.Document.Pages
            .Select(p => Norm(SourceText.Render(new SourceDocument(s.Document.Kind, [p]), int.MaxValue).Text)).ToList()).ToList();
        sourceNumbers = pages.SelectMany(p => p).SelectMany(t => Digits().Matches(t).Select(m => m.Value.TrimStart('0'))).ToHashSet();
    }

    public SlotResult Check(Slot slot, SlotResult result)
    {
        var warnings = result.Warnings.ToList();
        var evidence = result.Evidence.Select(Locate).ToList();
        var values = result.Value.Rows?.Values.ToList() ?? [result.Value.Text ?? ""];
        var filled = values.Where(v => v.Trim().Length > 0).ToList();

        if (filled.Count == 0 && slot.Required && !warnings.Any(w => w.StartsWith("Không tra được", StringComparison.Ordinal)))
            warnings.Add("Không tìm thấy thông tin trong nguồn");

        foreach (var problem in filled.Select(v => TypeProblem(slot, v)).OfType<string>().Distinct().Take(3)) warnings.Add(problem);

        if (result.Origin == ValueOrigin.Ai && filled.Count > 0)
        {
            if (slot.Strategy == SlotStrategy.Extract && !evidence.Any(e => e.Found)) warnings.Add("Không có căn cứ trong nguồn (trích dẫn không khớp)");
            else if (evidence.Any(e => !e.Found)) warnings.Add("Có trích dẫn không tìm thấy trong nguồn");
            if (slot.Strategy == SlotStrategy.Extract)
            {
                var invented = filled.SelectMany(v => Digits().Matches(v).Select(m => m.Value))
                    .Where(n => !sourceNumbers.Contains(n.TrimStart('0'))).Distinct().ToList();
                if (invented.Count > 0) warnings.Add($"Số không có trong nguồn: {string.Join(", ", invented.Take(5))}");
            }
            if (result.Confidence < reviewBelow) warnings.Add($"AI không chắc chắn (độ tin cậy {result.Confidence:0.##})");
        }
        return result with { Evidence = evidence, Warnings = warnings.Distinct().ToList() };
    }

    /// <summary>Recipe rules look across slots, so they run once every slot is checked.</summary>
    public static Dictionary<string, SlotResult> ApplyRules(IReadOnlyList<RecipeRule> rules, Dictionary<string, SlotResult> results)
    {
        foreach (var rule in rules)
        {
            if (!results.TryGetValue(rule.Slot, out var target)) continue;
            var other = rule.Other is null ? null : results.GetValueOrDefault(rule.Other);
            var problem = rule.Type switch
            {
                "dateAfter" => DateAfter(target.Value.Text, other?.Value.Text) ? null : rule.Message ?? $"Ngày phải sau ngày ở ô {rule.Other}",
                "sameSentenceCount" => SameSentences(target.Value, other?.Value) ? null : rule.Message ?? $"Số câu khác với ô {rule.Other}",
                "regex" => rule.Pattern is null || AllMatch(target.Value, rule.Pattern) ? null : rule.Message ?? "Giá trị không đúng mẫu quy định",
                _ => null,
            };
            if (problem is not null) results[rule.Slot] = target with { Warnings = [.. target.Warnings, problem] };
        }
        return results;
    }

    static bool DateAfter(string? value, string? other) =>
        !TryDate(value, out var a) || !TryDate(other, out var b) || a > b;

    static bool SameSentences(SlotValue a, SlotValue? b)
    {
        if (b is null) return true;
        static int Count(string? t) => string.IsNullOrWhiteSpace(t) ? 0 : SentenceEnd().Matches(t.Trim() + " ").Count;
        if (a.Rows is not null && b.Rows is not null)
            return a.Rows.All(r => !b.Rows.TryGetValue(r.Key, out var o) || Count(r.Value) == Count(o));
        return Count(a.Text) == Count(b.Text);
    }

    static bool AllMatch(SlotValue v, string pattern) =>
        (v.Rows?.Values ?? [v.Text ?? ""]).Where(t => t.Length > 0).All(t => Regex.IsMatch(t, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)));

    static string? TypeProblem(Slot slot, string value) => slot.ValueType switch
    {
        SlotValueType.Number when ExcelWriter.ParseNumber(value) is null => $"\"{Short(value)}\" không phải số",
        SlotValueType.Percent when ExcelWriter.ParseNumber(value.Trim().TrimEnd('%').Trim()) is null => $"\"{Short(value)}\" không phải tỷ lệ phần trăm",
        SlotValueType.Date when !TryDate(value, out _) => $"\"{Short(value)}\" không phải ngày hợp lệ (dd/MM/yyyy)",
        SlotValueType.Boolean or SlotValueType.Enum when slot.Options.Count > 0 && !slot.Options.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)
            => $"\"{Short(value)}\" không thuộc các giá trị cho phép ({string.Join(", ", slot.Options)})",
        _ => null,
    };

    public static bool TryDate(string? text, out DateTime date) =>
        DateTime.TryParseExact(text?.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>Finds the quote on the page the AI named, else anywhere in the sources; marks it found or not.</summary>
    Evidence Locate(Evidence e)
    {
        var quote = Norm(e.Quote);
        if (quote.Length == 0) return e with { Found = false };
        if (e.Source >= 0 && e.Source < pages.Count && e.Page >= 1 && e.Page <= pages[e.Source].Count && pages[e.Source][e.Page - 1].Contains(quote, StringComparison.Ordinal))
            return e with { Found = true };
        for (var s = 0; s < pages.Count; s++)
            for (var p = 0; p < pages[s].Count; p++)
                if (pages[s][p].Contains(quote, StringComparison.Ordinal)) return e with { Source = s, Page = p + 1, Found = true };
        return e with { Found = false };
    }

    /// <summary>Lower case, one space between words, table separators and cell addresses ignored, so a quote matches however the AI spaced it.</summary>
    static string Norm(string text)
    {
        var t = Regex.Replace(text.ToLowerInvariant(), @"\b[a-z]{1,3}\d+: ", " ");
        t = t.Replace(" | ", " ").Replace('“', '"').Replace('”', '"').Replace('’', '\'');
        return string.Join(' ', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    static string Short(string text) => text.Length > 40 ? text[..40] + "…" : text;
}
