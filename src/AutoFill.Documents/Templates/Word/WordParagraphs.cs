using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AutoFill.Documents.Templates.Word;

public enum ParagraphKind { Prose, Bullet, Numbered, List, Empty }

/// <summary>A run and the slice of the paragraph text it holds.</summary>
public sealed record WordRun(Run Element, int Start, string Text, bool Locked)
{
    public int End => Start + Text.Length;
}

/// <param name="Index">0-based, over the whole document: body, then headers, then footers</param>
/// <param name="Part">"body", "header" or "footer"</param>
/// <param name="TopLevel">a direct child of the body (not in a table): only these can form a rewritable section</param>
/// <param name="Cell">"bảng 1, hàng 2, ô 3" for a paragraph inside a table</param>
public sealed record WordParagraph(int Index, string Part, Paragraph Element, IReadOnlyList<WordRun> Runs, ParagraphKind Kind, bool TopLevel, string? Cell)
{
    public string Text { get; } = string.Concat(Runs.Select(r => r.Text));
}

/// <summary>
/// The one walk over a Word file that both the analysis (addresses shown to the AI) and the
/// writer (addresses written to) use, so paragraph numbers and character offsets always agree.
/// Text boxes are skipped (Word keeps two copies of them); text in a field result (DATE,
/// FORMTEXT...) is readable but locked, because Word would overwrite an edit.
/// </summary>
public static partial class WordParagraphs
{
    [GeneratedRegex(@"^\s*\d{1,2}\s*[/.)]\s")]
    private static partial Regex NumberedMark();

    [GeneratedRegex(@"^\s*[-–•+]\s")]
    private static partial Regex BulletMark();

    public static IReadOnlyList<WordParagraph> Collect(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("No main document part");
        var result = new List<WordParagraph>();
        if (main.Document?.Body is { } body) CollectPart(body, "body", result);
        foreach (var header in main.HeaderParts.OrderBy(p => p.Uri.OriginalString, StringComparer.Ordinal))
            if (header.Header is { } h) CollectPart(h, "header", result);
        foreach (var footer in main.FooterParts.OrderBy(p => p.Uri.OriginalString, StringComparer.Ordinal))
            if (footer.Footer is { } f) CollectPart(f, "footer", result);
        return result;
    }

    static void CollectPart(OpenXmlElement root, string part, List<WordParagraph> into)
    {
        var tables = root.Descendants<Table>().ToList();
        var fields = new Stack<bool>(); // one entry per open complex field: true once in its result
        foreach (var p in root.Descendants<Paragraph>())
        {
            if (p.Ancestors<TextBoxContent>().Any() || p.Ancestors().Any(a => a.LocalName == "Fallback")) continue;
            var runs = Runs(p, fields);
            var text = string.Concat(runs.Select(r => r.Text));
            into.Add(new WordParagraph(into.Count, part, p, runs, KindOf(p, text), p.Parent == root && part == "body", CellOf(p, tables)));
        }
    }

    /// <summary>Runs of one paragraph with their text offsets. Pass no field stack to ignore field locking.</summary>
    public static List<WordRun> Runs(Paragraph p, Stack<bool>? fields = null)
    {
        var runs = new List<WordRun>();
        var offset = 0;
        foreach (var (run, inSimpleField) in RunElements(p, false))
        {
            if (run.GetFirstChild<FieldChar>() is { } fc && fields is not null)
            {
                var type = fc.FieldCharType?.Value;
                if (type == FieldCharValues.Begin) fields.Push(false);
                else if (type == FieldCharValues.Separate && fields.Count > 0) { fields.Pop(); fields.Push(true); }
                else if (type == FieldCharValues.End && fields.Count > 0) fields.Pop();
                continue;
            }
            var text = RunText(run);
            if (text.Length == 0) continue;
            runs.Add(new WordRun(run, offset, text, inSimpleField || fields is { Count: > 0 }));
            offset += text.Length;
        }
        return runs;
    }

    /// <summary>Runs in order, inside hyperlinks, insertions, content controls...; never inside a nested paragraph.</summary>
    static IEnumerable<(Run Run, bool InSimpleField)> RunElements(OpenXmlElement node, bool inSimpleField)
    {
        foreach (var child in node.ChildElements)
        {
            switch (child)
            {
                case Run r: yield return (r, inSimpleField); break;
                case Paragraph or DeletedRun: break;
                case Hyperlink or InsertedRun or MoveToRun or SimpleField or SdtRun or SdtContentRun or CustomXmlRun:
                case { LocalName: "smartTag" }:
                    foreach (var x in RunElements(child, inSimpleField || child is SimpleField)) yield return x;
                    break;
            }
        }
    }

    /// <summary>The characters a run shows; page and column breaks are layout, not text.</summary>
    public static string RunText(Run run)
    {
        var sb = new StringBuilder();
        foreach (var child in run.ChildElements) sb.Append(ChildText(child));
        return sb.ToString();
    }

    public static string ChildText(OpenXmlElement child) => child switch
    {
        Text t => t.Text,
        TabChar => "\t",
        Break b when b.Type is null || b.Type.Value == BreakValues.TextWrapping => "\n",
        CarriageReturn => "\n",
        NoBreakHyphen => "-",
        _ => "",
    };

    public static ParagraphKind KindOf(Paragraph p, string text)
    {
        if (text.Trim().Length == 0) return ParagraphKind.Empty;
        if (p.ParagraphProperties?.NumberingProperties is not null) return ParagraphKind.List;
        if (NumberedMark().IsMatch(text)) return ParagraphKind.Numbered;
        if (BulletMark().IsMatch(text)) return ParagraphKind.Bullet;
        return ParagraphKind.Prose;
    }

    /// <summary>The written kind of a new line: "1. ..." numbered, "- ..." bullet, otherwise prose.</summary>
    public static ParagraphKind KindOfLine(string line) =>
        line.Trim().Length == 0 ? ParagraphKind.Empty
        : NumberedMark().IsMatch(line) ? ParagraphKind.Numbered
        : BulletMark().IsMatch(line) ? ParagraphKind.Bullet
        : ParagraphKind.Prose;

    /// <summary>"- " / "1. " at the start of a line, which Word numbering already draws.</summary>
    public static string StripListMark(string line) => BulletMark().Replace(NumberedMark().Replace(line, ""), "");

    static string? CellOf(Paragraph p, List<Table> tables)
    {
        if (p.Ancestors<TableCell>().FirstOrDefault() is not { } cell || cell.Parent is not TableRow row || row.Parent is not Table table) return null;
        var r = table.Elements<TableRow>().ToList().IndexOf(row) + 1;
        var c = row.Elements<TableCell>().ToList().IndexOf(cell) + 1;
        return $"bảng {tables.IndexOf(table) + 1}, hàng {r}, ô {c}";
    }
}
