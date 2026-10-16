using System.Text.Json.Nodes;
using AutoFill.Core;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using PageSize = UglyToad.PdfPig.Content.PageSize;
using Run = DocumentFormat.OpenXml.Wordprocessing.Run;

namespace AutoFill.Tests;

/// <summary>Source files built in code, so tests never depend on real (personal) documents.</summary>
public static class SourceFixtures
{
    /// <param name="pages">text per page; null = a page with no text layer (a scan)</param>
    public static byte[] Pdf(params string?[] pages)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 780.0;
            foreach (var line in text?.Split('\n') ?? [])
            {
                page.AddText(line, 11, new PdfPoint(40, y), font);
                y -= 16;
            }
        }
        return builder.Build();
    }

    /// <summary>Two pages (explicit page break), Vietnamese text, a 2×2 table on page 1.</summary>
    public static byte[] Docx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            static Paragraph P(string text) => new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
            static TableCell Cell(string text) => new(P(text));
            main.Document = new Document(new Body(
                P("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM"),
                P(""),
                new Table(
                    new TableRow(Cell("Họ tên"), Cell("Nguyễn Văn A")),
                    new TableRow(Cell("Số hộ chiếu"), Cell("C1234567"))),
                new Paragraph(new Run(new Text("Hết trang một")), new Run(new Break { Type = BreakValues.Page })),
                P("Trang hai: kính gửi Sở Ngoại vụ")));
        }
        return ms.ToArray();
    }

    /// <summary>Sheet "EET" with a header, a number with a format and a formula; an empty second sheet.</summary>
    public static byte[] Xlsx()
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("EET");
        sheet.Cell("A1").Value = "ISIN";
        sheet.Cell("B1").Value = "Tỷ lệ";
        sheet.Cell("A2").Value = "LU0000000001";
        sheet.Cell("B2").Value = 0.125;
        sheet.Cell("B2").Style.NumberFormat.Format = "0.0%";
        sheet.Cell("C2").FormulaA1 = "B2*2";
        book.AddWorksheet("Trống");
        using var ms = new MemoryStream();
        book.SaveAs(ms);
        return ms.ToArray();
    }

    public static int PageCount(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }
}

/// <summary>Stands in for the AI: answers "chép lại" requests with one text per page received, and counts calls.</summary>
public sealed class FakeScanAi : IAiClient
{
    public List<AiRequest> Requests { get; } = [];
    /// <summary>Pages to answer with regardless of the file (to test a model that splits or merges pages).</summary>
    public int? ForcePageCount { get; set; }

    public Task<JsonNode> GenerateJsonAsync(AiRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        var file = request.Files!.Single();
        var count = ForcePageCount ?? (file.ContentType == "application/pdf" ? SourceFixtures.PageCount(file.Content) : 1);
        var pages = new JsonArray();
        for (var i = 1; i <= count; i++) pages.Add(new JsonObject { ["text"] = $"Bản scan {i} của lượt {Requests.Count}\nHọ tên | Trần Thị B" });
        return Task.FromResult<JsonNode>(new JsonObject { ["pages"] = pages });
    }
}
