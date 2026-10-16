using System.Text;
using System.Text.Json.Serialization;

namespace AutoFill.Documents.Ingest;

public enum SourceKind { Pdf, Docx, Doc, Xlsx, Xls, Image }

/// <summary>
/// Any source file, normalized: pages of blocks in reading order. Every value the AI later
/// fills in points back to a page (and a cell address for spreadsheets) of this document.
/// </summary>
public sealed record SourceDocument(SourceKind Kind, IReadOnlyList<SourcePage> Pages)
{
    /// <summary>Things the user should know about how the file was read (pages read by AI, conversion...).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public int CharCount => Pages.Sum(p => p.CharCount);
}

/// <param name="Number">1-based; for a spreadsheet, the sheet's position</param>
/// <param name="Label">sheet name for spreadsheets</param>
/// <param name="ReadByAi">the page was a scan (or image) and the AI transcribed it</param>
public sealed record SourcePage(int Number, IReadOnlyList<SourceBlock> Blocks, string? Label = null, bool ReadByAi = false)
{
    [JsonIgnore]
    public int CharCount => Blocks.Sum(b => b.Text?.Length ?? b.Rows?.Sum(r => r.Sum(c => c.Text.Length)) ?? 0);
}

/// <summary>Either a paragraph (<see cref="Text"/>) or a table (<see cref="Rows"/>).</summary>
public sealed record SourceBlock
{
    public string? Text { get; init; }
    public IReadOnlyList<IReadOnlyList<SourceCell>>? Rows { get; init; }

    public static SourceBlock Paragraph(string text) => new() { Text = text };
    public static SourceBlock Table(IReadOnlyList<IReadOnlyList<SourceCell>> rows) => new() { Rows = rows };
}

/// <param name="Ref">spreadsheet cell address such as "B7"; null for tables in documents</param>
public sealed record SourceCell(string Text, string? Ref = null);

/// <summary>The document as plain text for prompts, with page markers the AI quotes back as evidence.</summary>
public static class SourceText
{
    /// <summary>
    /// Drops control characters other than tab and newline (PDF text layers carry NULs, which
    /// PostgreSQL jsonb refuses) and trims.
    /// </summary>
    public static string Clean(string text)
    {
        if (!text.Any(c => char.IsControl(c) && c is not ('\t' or '\n'))) return text.Trim();
        return new string(text.Where(c => !char.IsControl(c) || c is '\t' or '\n').ToArray()).Trim();
    }

    /// <returns>the text, and whether it was cut at <paramref name="maxChars"/></returns>
    public static (string Text, bool Truncated) Render(SourceDocument doc, int maxChars)
    {
        var sb = new StringBuilder();
        foreach (var page in doc.Pages)
        {
            sb.Append(page.Label is { } label ? $"--- Trang {page.Number}: {label} ---\n" : $"--- Trang {page.Number} ---\n");
            foreach (var block in page.Blocks)
            {
                if (block.Text is { } text) sb.Append(text).Append('\n');
                foreach (var row in block.Rows ?? [])
                    sb.Append(string.Join(" | ", row.Select(c => c.Ref is null ? c.Text : $"{c.Ref}: {c.Text}"))).Append('\n');
            }
            sb.Append('\n');
            if (sb.Length > maxChars) return (sb.ToString(0, maxChars), true);
        }
        return (sb.ToString(), false);
    }
}
