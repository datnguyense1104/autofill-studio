using System.Text;
using AutoFill.Core;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AutoFill.Documents.Ingest;

/// <summary>
/// Word body → paragraphs and tables in order. Word has no stored pages, so a new page starts
/// only at an explicit page break; page numbers are therefore approximate.
/// </summary>
public static class DocxReader
{
    public static SourceDocument Read(byte[] content, SourceKind kind = SourceKind.Docx)
    {
        WordprocessingDocument doc;
        try
        {
            doc = WordprocessingDocument.Open(new MemoryStream(content), false);
        }
        catch (Exception err) when (err is OpenXmlPackageException or InvalidDataException or FileFormatException)
        {
            throw new UserFacingException("File Word bị hỏng hoặc không đúng định dạng .docx");
        }
        using (doc)
        {
            var body = doc.MainDocumentPart?.Document?.Body ?? throw new UserFacingException("File Word không có nội dung");
            var pages = new List<SourcePage>();
            var blocks = new List<SourceBlock>();

            void NewPage()
            {
                if (blocks.Count == 0) return;
                pages.Add(new SourcePage(pages.Count + 1, blocks));
                blocks = [];
            }

            foreach (var element in body.ChildElements)
            {
                switch (element)
                {
                    case Paragraph p:
                        if (p.ParagraphProperties?.PageBreakBefore is { } before && (before.Val?.Value ?? true)) NewPage();
                        var text = SourceText.Clean(ParagraphText(p));
                        if (text.Length > 0) blocks.Add(SourceBlock.Paragraph(text));
                        if (HasPageBreak(p)) NewPage();
                        break;
                    case Table t:
                        var rows = TableRows(t);
                        if (rows.Count > 0) blocks.Add(SourceBlock.Table(rows));
                        break;
                }
            }
            NewPage();
            if (pages.Count == 0) pages.Add(new SourcePage(1, []));
            return new SourceDocument(kind, pages);
        }
    }

    static bool HasPageBreak(Paragraph p) =>
        p.Descendants<Break>().Any(b => b.Type?.Value == BreakValues.Page);

    /// <summary>Visible text: runs, tabs and line breaks (tracked deletions are w:delText, so they are left out).</summary>
    static string ParagraphText(OpenXmlElement p)
    {
        var sb = new StringBuilder();
        foreach (var node in p.Descendants())
        {
            switch (node)
            {
                case Text t: sb.Append(t.Text); break;
                case TabChar: sb.Append('\t'); break;
                case Break b when b.Type is null || b.Type.Value == BreakValues.TextWrapping: sb.Append('\n'); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Rows of cell text; a nested table is flattened into its cell's text.</summary>
    static List<IReadOnlyList<SourceCell>> TableRows(Table t)
    {
        var rows = new List<IReadOnlyList<SourceCell>>();
        foreach (var row in t.Elements<TableRow>())
        {
            var cells = row.Elements<TableCell>()
                .Select(c => new SourceCell(string.Join("\n", c.Descendants<Paragraph>().Select(ParagraphText).Select(SourceText.Clean).Where(s => s.Length > 0))))
                .ToList();
            if (cells.Any(c => c.Text.Length > 0)) rows.Add(cells);
        }
        return rows;
    }
}
