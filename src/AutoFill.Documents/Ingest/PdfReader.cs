using AutoFill.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Writer;

namespace AutoFill.Documents.Ingest;

/// <summary>
/// Text layer of a PDF, page by page. A page with almost no text (a scan) or with unreadable
/// characters (a broken font) is left for the AI to read from the page image.
/// </summary>
public static class PdfReader
{
    /// <summary>Fewer visible characters than this and the page is treated as a scan.</summary>
    public const int MinTextChars = 20;

    /// <returns>one entry per page: its text blocks, or null when the page needs the AI</returns>
    public static IReadOnlyList<List<SourceBlock>?> ReadTextLayer(byte[] content)
    {
        using var pdf = Open(content);
        var pages = new List<List<SourceBlock>?>();
        foreach (var page in pdf.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            pages.Add(LooksReadable(text) ? Lines(text) : null);
        }
        return pages;
    }

    /// <summary>A new PDF holding only the given pages (1-based), to send a few scanned pages to the AI.</summary>
    public static byte[] ExtractPages(byte[] content, IEnumerable<int> pageNumbers)
    {
        using var pdf = Open(content);
        using var builder = new PdfDocumentBuilder();
        foreach (var n in pageNumbers) builder.AddPage(pdf, n);
        return builder.Build();
    }

    static PdfDocument Open(byte[] content)
    {
        try
        {
            return PdfDocument.Open(content);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new UserFacingException("File PDF có mật khẩu, hãy gỡ mật khẩu rồi tải lại");
        }
        catch (Exception err) when (err is PdfDocumentFormatException or InvalidOperationException or ArgumentException)
        {
            throw new UserFacingException("File PDF bị hỏng hoặc không đúng định dạng");
        }
    }

    /// <summary>Enough text, and not the replacement/private-use characters a broken font produces.</summary>
    public static bool LooksReadable(string text)
    {
        int visible = 0, bad = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            visible++;
            if (c == '�' || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.PrivateUse
                    or System.Globalization.UnicodeCategory.Control) bad++;
        }
        return visible >= MinTextChars && bad * 20 < visible;
    }

    /// <summary>Each non-empty line becomes a block, so evidence quotes stay short and findable.</summary>
    public static List<SourceBlock> Lines(string text) =>
        text.Split('\n').Select(SourceText.Clean).Where(l => l.Length > 0).Select(SourceBlock.Paragraph).ToList();
}
