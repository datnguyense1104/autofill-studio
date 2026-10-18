using System.Text.RegularExpressions;
using AutoFill.Documents.Templates.Excel;
using AutoFill.Documents.Templates.Word;

namespace AutoFill.Documents.Templates;

/// <summary>
/// How alike two templates read, to catch a form that is already a recipe (often a filled copy
/// of it, with other names and dates). Word pairs are compared, so shared boilerplate alone
/// (letterhead, "CỘNG HÒA XÃ HỘI...") stays far below <see cref="SameForm"/>.
/// </summary>
public static partial class TemplateSimilarity
{
    /// <summary>
    /// From this score on, two templates are taken as the same form. Measured on real letters:
    /// two copies of one form ≈ 0.9, different forms of the same office 0.1–0.3.
    /// </summary>
    public const double SameForm = 0.6;

    /// <summary>All the text of a .docx/.xlsx template, in reading order.</summary>
    public static string TextOf(byte[] template, TemplateKind kind) => kind == TemplateKind.Word
        ? string.Join('\n', WordOutline.Read(template).Select(p => p.Text))
        : string.Join('\n', ExcelOutline.Read(template).SelectMany(s => s.Cells).Select(c => c.Text));

    /// <returns>0 (nothing in common) to 1 (same words in the same order)</returns>
    public static double Score(string a, string b)
    {
        var x = Pairs(a);
        var y = Pairs(b);
        if (x.Count + y.Count == 0) return 0;
        return 2.0 * x.Count(y.Contains) / (x.Count + y.Count);
    }

    static HashSet<string> Pairs(string text)
    {
        var words = Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();
        return Enumerable.Range(0, Math.Max(0, words.Count - 1)).Select(i => $"{words[i]} {words[i + 1]}").ToHashSet();
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}
