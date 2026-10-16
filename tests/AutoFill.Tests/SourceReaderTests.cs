using AutoFill.Core;
using AutoFill.Documents.Ingest;

namespace AutoFill.Tests;

public sealed class SourceReaderTests
{
    readonly FakeScanAi ai = new();
    readonly LegacyConverter legacy = new(new LegacyConverterOptions(null, TimeSpan.FromSeconds(90)));
    SourceReader Reader => new(new ScanTranscriber(ai), legacy);

    static string AllText(SourceDocument doc) => SourceText.Render(doc, int.MaxValue).Text;

    [Fact]
    public async Task A_pdf_with_text_is_read_page_by_page_without_the_ai()
    {
        var doc = await Reader.ReadAsync(SourceFixtures.Pdf("Ministry of Foreign Affairs\nNote verbale No. 123/2026", "Second page: passport list follows"), "pdf");

        Assert.Equal(SourceKind.Pdf, doc.Kind);
        Assert.Equal([1, 2], doc.Pages.Select(p => p.Number));
        Assert.Contains(doc.Pages[0].Blocks, b => b.Text == "Note verbale No. 123/2026");
        Assert.Contains("--- Trang 2 ---\nSecond page: passport list follows", AllText(doc));
        Assert.Empty(ai.Requests);
        Assert.Empty(doc.Notes);
    }

    [Fact]
    public async Task Only_scanned_pages_go_to_the_ai_a_few_pages_per_call()
    {
        // Page 1 has text, pages 2-8 are scans: 7 scanned pages = calls of 5 + 2.
        var pdf = SourceFixtures.Pdf(["Cover letter with enough text on it", .. Enumerable.Repeat<string?>(null, 7)]);
        var progress = new List<(string, int, int)>();
        var doc = await Reader.ReadAsync(pdf, "pdf", (s, d, t) => { progress.Add((s, d, t)); return Task.CompletedTask; });

        Assert.Equal(8, doc.Pages.Count);
        Assert.Equal([5, 2], ai.Requests.Select(r => SourceFixtures.PageCount(r.Files![0].Content)));
        Assert.False(doc.Pages[0].ReadByAi);
        Assert.All(doc.Pages.Skip(1), p => Assert.True(p.ReadByAi));
        // Page 7 is the 1st page of the 2nd call.
        Assert.Equal("Bản scan 1 của lượt 2", doc.Pages[6].Blocks[0].Text);
        // "a | b" lines become a table.
        Assert.Equal(["Họ tên", "Trần Thị B"], doc.Pages[6].Blocks[1].Rows![0].Select(c => c.Text));
        Assert.Equal(["7/8 trang là bản scan, AI đã chép lại: 2–8"], doc.Notes);
        Assert.Equal(("AI đọc trang scan", 7, 7), progress[^1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Pages_stay_aligned_when_the_ai_returns_too_few_or_too_many(int answered)
    {
        ai.ForcePageCount = answered;
        var doc = await Reader.ReadAsync(SourceFixtures.Pdf(null, null), "pdf");

        Assert.Equal([1, 2], doc.Pages.Select(p => p.Number));
        Assert.Equal("Bản scan 1 của lượt 1", doc.Pages[0].Blocks[0].Text);
        if (answered == 1) Assert.Empty(doc.Pages[1].Blocks);
        else Assert.Contains(doc.Pages[1].Blocks, b => b.Text == "Bản scan 4 của lượt 1");
    }

    [Fact]
    public async Task A_word_file_keeps_paragraphs_tables_and_page_breaks()
    {
        var doc = await Reader.ReadAsync(SourceFixtures.Docx(), "docx");

        Assert.Equal(2, doc.Pages.Count);
        Assert.Equal("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", doc.Pages[0].Blocks[0].Text);
        var table = doc.Pages[0].Blocks[1].Rows!;
        Assert.Equal(["Số hộ chiếu", "C1234567"], table[1].Select(c => c.Text));
        Assert.Equal("Trang hai: kính gửi Sở Ngoại vụ", doc.Pages[1].Blocks.Single().Text);
        Assert.Contains("Họ tên | Nguyễn Văn A", AllText(doc));
    }

    [Fact]
    public async Task An_excel_file_gives_one_page_per_sheet_with_cell_addresses()
    {
        var doc = await Reader.ReadAsync(SourceFixtures.Xlsx(), "xlsx");

        Assert.Equal(["EET", "Trống"], doc.Pages.Select(p => p.Label));
        var rows = doc.Pages[0].Blocks.Single().Rows!;
        Assert.Equal([("A2", "LU0000000001"), ("B2", "12.5%"), ("C2", "0.25")], rows[1].Select(c => (c.Ref!, c.Text)));
        Assert.Empty(doc.Pages[1].Blocks);
        Assert.Contains("--- Trang 1: EET ---\nA1: ISIN | B1: Tỷ lệ", AllText(doc));
    }

    [Fact]
    public async Task A_photo_is_read_by_the_ai()
    {
        var doc = await Reader.ReadAsync([0x89, 0x50, 0x4E, 0x47], "png");

        Assert.Equal(SourceKind.Image, doc.Kind);
        Assert.True(doc.Pages.Single().ReadByAi);
        Assert.Equal("image/png", ai.Requests.Single().Files![0].ContentType);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("docx")]
    [InlineData("xlsx")]
    public async Task A_broken_file_gives_a_message_for_the_user(string ext)
    {
        var err = await Assert.ThrowsAsync<UserFacingException>(() => Reader.ReadAsync("not a real file"u8.ToArray(), ext));
        Assert.Contains("hỏng", err.Message);
    }

    [Fact]
    public async Task Legacy_files_need_libreoffice()
    {
        var missing = new SourceReader(new ScanTranscriber(ai), new LegacyConverter(new LegacyConverterOptions("Z:/no/soffice", TimeSpan.FromSeconds(5))));
        var err = await Assert.ThrowsAsync<UserFacingException>(() => missing.ReadAsync([1, 2, 3], "doc"));
        Assert.Contains("LibreOffice", err.Message);
    }

    /// <summary>Runs only where LibreOffice is installed (the Docker image); elsewhere it passes without checking.</summary>
    [Fact]
    public async Task A_doc_file_is_converted_then_read_when_libreoffice_is_installed()
    {
        if (legacy.Soffice is null) return;
        // Make a real .doc by converting a .docx backwards with the same LibreOffice.
        var docx = SourceFixtures.Docx();
        var dir = Directory.CreateTempSubdirectory("autofill-doc-test-");
        try
        {
            var input = Path.Combine(dir.FullName, "in.docx");
            await File.WriteAllBytesAsync(input, docx);
            var p = System.Diagnostics.Process.Start(legacy.Soffice, ["--headless", "--convert-to", "doc", "--outdir", dir.FullName, input]);
            await p.WaitForExitAsync();
            var doc = await Reader.ReadAsync(await File.ReadAllBytesAsync(Path.Combine(dir.FullName, "in.doc")), "doc");
            Assert.Equal(SourceKind.Doc, doc.Kind);
            Assert.Contains("Nguyễn Văn A", AllText(doc));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Long_text_is_cut_and_says_so()
    {
        var doc = new SourceDocument(SourceKind.Pdf, [new SourcePage(1, [SourceBlock.Paragraph(new string('a', 500))])]);
        Assert.Equal((false, 518),(SourceText.Render(doc, 1000).Truncated, SourceText.Render(doc, 1000).Text.Length));
        var (text, truncated) = SourceText.Render(doc, 100);
        Assert.True(truncated);
        Assert.Equal(100, text.Length);
    }

    [Fact]
    public void Text_layers_with_nul_or_broken_fonts_are_handled()
    {
        Assert.Equal("ab", SourceText.Clean(" a\0b\r "));
        Assert.False(PdfReader.LooksReadable(new string('\uE000', 50)));
        Assert.False(PdfReader.LooksReadable("short"));
        Assert.True(PdfReader.LooksReadable("Kính gửi: Sở Ngoại vụ Thành phố"));
        Assert.Equal("1–3, 7, 9–10", SourceReader.Ranges([1, 2, 3, 7, 9, 10]));
    }
}
