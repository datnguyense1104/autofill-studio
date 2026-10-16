using AutoFill.Documents.Templates.Excel;
using AutoFill.Documents.Templates.Word;

namespace AutoFill.Documents.Templates;

/// <summary>A phrase of template paragraph <see cref="Paragraph"/> that reads differently in another filled copy.</summary>
public sealed record PhraseDiff(int Paragraph, string Text);

/// <summary>Template paragraphs [From, To] that another filled copy replaces with a different number of paragraphs: likely a rewritten body.</summary>
public sealed record BlockDiff(int From, int To);

public sealed record CellDiff(string Sheet, string Cell);

/// <summary>
/// What changes between copies of the same template filled for different cases. Found by code
/// (no AI), these are near-certain slots; the AI only names them.
/// </summary>
public static class TemplateDiff
{
    public static (List<PhraseDiff> Phrases, List<BlockDiff> Blocks) Word(IReadOnlyList<OutlineParagraph> template, IEnumerable<IReadOnlyList<OutlineParagraph>> filled)
    {
        var phrases = new List<PhraseDiff>();
        var blocks = new List<BlockDiff>();
        var a = template.Where(p => p.Kind != ParagraphKind.Empty).ToList();
        foreach (var other in filled)
        {
            var b = other.Where(p => p.Kind != ParagraphKind.Empty).ToList();
            foreach (var (fromA, toA, fromB, toB) in ChangedRuns(a.Select(p => Norm(p.Text)).ToList(), b.Select(p => Norm(p.Text)).ToList()))
            {
                int sa = fromA, ea = toA, sb = fromB, eb = toB;
                void Pair(int i, int j)
                {
                    if (Middle(a[i].Text, b[j].Text) is { Length: > 0 } middle) phrases.Add(new PhraseDiff(a[i].Index, middle));
                }
                // Paragraphs that mostly match at either end of the run are edited lines, not part of a rewritten body.
                while (sa < ea && sb < eb && (ea - sa == eb - sb || Similar(a[sa].Text, b[sb].Text))) Pair(sa++, sb++);
                while (sa < ea && sb < eb && Similar(a[ea - 1].Text, b[eb - 1].Text)) Pair(--ea, --eb);
                // Left only in the template (or both sides): a block; only added in the other copy: nothing to point at.
                if (sa < ea) blocks.Add(new BlockDiff(a[sa].Index, a[ea - 1].Index));
            }
        }
        return (phrases.Distinct().ToList(), blocks.Distinct().ToList());
    }

    public static List<CellDiff> Excel(IReadOnlyList<OutlineSheet> template, IEnumerable<IReadOnlyList<OutlineSheet>> filled)
    {
        var diffs = new HashSet<CellDiff>();
        foreach (var other in filled)
        {
            foreach (var sheet in template)
            {
                var otherSheet = other.FirstOrDefault(s => s.Name == sheet.Name);
                if (otherSheet is null) continue;
                var mine = sheet.Cells.Where(c => !c.HasFormula).ToDictionary(c => c.Address, c => c.Text);
                var theirs = otherSheet.Cells.Where(c => !c.HasFormula).ToDictionary(c => c.Address, c => c.Text);
                foreach (var address in mine.Keys.Union(theirs.Keys))
                {
                    if (otherSheet.Find(address)?.HasFormula == true || sheet.Find(address)?.HasFormula == true) continue;
                    if (mine.GetValueOrDefault(address, "") != theirs.GetValueOrDefault(address, "")) diffs.Add(new CellDiff(sheet.Name, address));
                }
            }
        }
        return diffs.OrderBy(d => d.Sheet).ThenBy(d => d.Cell, StringComparer.Ordinal).ToList();
    }

    /// <summary>Same start and end over at least half the length: one line with a few words changed.</summary>
    static bool Similar(string a, string b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        return (prefix + suffix) * 2 >= Math.Max(a.Length, b.Length);
    }

    static string Norm(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The part of <paramref name="a"/> that differs from <paramref name="b"/>, widened to whole
    /// tokens (so "01/02/2026" stays one date), without the sentence's closing punctuation.
    /// </summary>
    public static string? Middle(string a, string b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        var start = prefix;
        var end = a.Length - suffix;
        static bool InToken(char c) => !char.IsWhiteSpace(c) && c is not (',' or ';' or '(' or ')' or '"' or '“' or '”');
        while (start > 0 && InToken(a[start - 1])) start--;
        while (end < a.Length && InToken(a[end])) end++;
        var middle = a[start..end].Trim().TrimEnd('.', ':', ',', ';');
        return middle.Length == 0 ? null : middle;
    }

    /// <summary>Runs of lines that differ between two lists, aligned by longest common subsequence: (fromA, toA, fromB, toB), ends exclusive.</summary>
    static IEnumerable<(int, int, int, int)> ChangedRuns(List<string> a, List<string> b)
    {
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int x = 0, y = 0, startX = 0, startY = 0;
        while (x < a.Count || y < b.Count)
        {
            if (x < a.Count && y < b.Count && a[x] == b[y])
            {
                if (startX < x || startY < y) yield return (startX, x, startY, y);
                x++; y++;
                startX = x; startY = y;
            }
            else if (y < b.Count && (x == a.Count || lcs[x, y + 1] >= lcs[x + 1, y])) y++;
            else x++;
        }
        if (startX < x || startY < y) yield return (startX, x, startY, y);
    }
}
