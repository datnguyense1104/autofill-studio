using System.Text;
using AutoFill.Core;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace AutoFill.Documents.Templates.Word;

/// <summary>A paragraph as the analysis sees it (no Open XML objects, safe to keep after the file is closed).</summary>
public sealed record OutlineParagraph(int Index, string Part, ParagraphKind Kind, bool TopLevel, string? Cell, string Text);

public static class WordOutline
{
    public static IReadOnlyList<OutlineParagraph> Read(byte[] template)
    {
        WordprocessingDocument doc;
        try
        {
            doc = WordprocessingDocument.Open(new MemoryStream(template), false);
        }
        catch (Exception err) when (err is OpenXmlPackageException or InvalidDataException or FileFormatException)
        {
            throw new UserFacingException("File mẫu Word bị hỏng hoặc không đúng định dạng .docx");
        }
        using (doc)
        {
            return WordParagraphs.Collect(doc)
                .Select(p => new OutlineParagraph(p.Index, p.Part, p.Kind, p.TopLevel, p.Cell, p.Text))
                .ToList();
        }
    }

    /// <summary>
    /// The paragraph text as shown to the AI (line break → " ⏎ ", tab → space), with, for each
    /// shown character, its offset in the real text, so a phrase the AI copies maps back exactly.
    /// </summary>
    public static (string Text, int[] Map) Display(string text)
    {
        var sb = new StringBuilder();
        var map = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            var shown = text[i] switch { '\n' => " ⏎ ", '\t' => " ", var c => c.ToString() };
            sb.Append(shown);
            for (var k = 0; k < shown.Length; k++) map.Add(i);
        }
        map.Add(text.Length);
        return (sb.ToString(), [.. map]);
    }

    static string KindLabel(ParagraphKind kind) => kind switch
    {
        ParagraphKind.Bullet => " [gạch đầu dòng]",
        ParagraphKind.Numbered => " [đánh số]",
        ParagraphKind.List => " [danh sách Word]",
        _ => "",
    };

    /// <summary>
    /// One line per non-empty paragraph, "P12 [đánh số] (bảng 1, hàng 2, ô 1): text", for the AI
    /// to point at. Very long paragraphs are shortened; the numbers never change.
    /// </summary>
    public static string Render(IReadOnlyList<OutlineParagraph> paragraphs, int maxParagraphChars = 1500)
    {
        var sb = new StringBuilder();
        string? part = null;
        foreach (var p in paragraphs.Where(p => p.Kind != ParagraphKind.Empty))
        {
            if (p.Part != part)
            {
                sb.Append(p.Part switch { "header" => "## Đầu trang\n", "footer" => "## Chân trang\n", _ => "## Nội dung\n" });
                part = p.Part;
            }
            var text = Display(p.Text).Text;
            if (text.Length > maxParagraphChars) text = text[..maxParagraphChars] + " …(cắt bớt)";
            sb.Append($"P{p.Index}{KindLabel(p.Kind)}{(p.Cell is null ? "" : $" ({p.Cell})")}: {text}\n");
        }
        return sb.ToString();
    }
}
