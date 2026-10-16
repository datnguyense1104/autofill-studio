namespace AutoFill.Documents.Ingest;

/// <summary>Any accepted file → <see cref="SourceDocument"/>, choosing the reader by extension.</summary>
public sealed class SourceReader(ScanTranscriber scans, LegacyConverter legacy)
{
    /// <summary>
    /// Bump when a reader changes what it produces: cached documents from an older version are
    /// read again instead of being reused.
    /// </summary>
    public const int Version = 1;

    /// <param name="ext">lower-case extension without the dot (pdf, docx, doc, xlsx, xls, png, jpg, jpeg)</param>
    /// <param name="onProgress">(step, done, total) for the job's progress line</param>
    public async Task<SourceDocument> ReadAsync(byte[] content, string ext, Func<string, int, int, Task>? onProgress = null, CancellationToken ct = default)
    {
        switch (ext)
        {
            case "pdf": return await ReadPdfAsync(content, onProgress, ct);
            case "docx": return DocxReader.Read(content);
            case "xlsx": return XlsxReader.Read(content);
            case "doc":
                return DocxReader.Read(await legacy.ConvertAsync(content, "doc", ct), SourceKind.Doc)
                    with { Notes = ["File .doc được chuyển sang .docx bằng LibreOffice trước khi đọc"] };
            case "xls":
                return XlsxReader.Read(await legacy.ConvertAsync(content, "xls", ct), SourceKind.Xls)
                    with { Notes = ["File .xls được chuyển sang .xlsx bằng LibreOffice trước khi đọc"] };
            case "png" or "jpg" or "jpeg":
                if (onProgress is not null) await onProgress("AI đọc ảnh", 0, 1);
                var page = await scans.TranscribeImageAsync(content, ext == "png" ? "image/png" : "image/jpeg", ct);
                return new SourceDocument(SourceKind.Image, [page]) { Notes = ["Ảnh được AI chép lại"] };
            default:
                throw new ArgumentException($"No reader for .{ext}", nameof(ext));
        }
    }

    /// <summary>Text-layer pages are read directly; only scanned pages go to the AI.</summary>
    async Task<SourceDocument> ReadPdfAsync(byte[] content, Func<string, int, int, Task>? onProgress, CancellationToken ct)
    {
        var layer = PdfReader.ReadTextLayer(content);
        var pages = layer.Select((blocks, i) => blocks is null ? null : new SourcePage(i + 1, blocks)).ToArray();
        var scanned = pages.Select((p, i) => (p, n: i + 1)).Where(x => x.p is null).Select(x => x.n).ToList();
        if (scanned.Count == 0) return new SourceDocument(SourceKind.Pdf, pages!);

        if (onProgress is not null) await onProgress("AI đọc trang scan", 0, scanned.Count);
        var read = await scans.TranscribePdfPagesAsync(content, scanned,
            onProgress is null ? null : (done, total) => onProgress("AI đọc trang scan", done, total), ct);
        foreach (var page in read) pages[page.Number - 1] = page;
        var note = scanned.Count == pages.Length
            ? "File PDF là bản scan, AI đã chép lại toàn bộ"
            : $"{scanned.Count}/{pages.Length} trang là bản scan, AI đã chép lại: {Ranges(scanned)}";
        return new SourceDocument(SourceKind.Pdf, pages!) { Notes = [note] };
    }

    /// <summary>[1,2,3,7,9,10] → "1–3, 7, 9–10".</summary>
    public static string Ranges(IReadOnlyList<int> numbers)
    {
        var parts = new List<string>();
        for (var i = 0; i < numbers.Count;)
        {
            var j = i;
            while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1) j++;
            parts.Add(i == j ? $"{numbers[i]}" : $"{numbers[i]}–{numbers[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
