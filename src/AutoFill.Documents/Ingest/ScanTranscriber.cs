using System.Text.Json.Nodes;
using AutoFill.Core;

namespace AutoFill.Documents.Ingest;

/// <summary>
/// Scanned pages and photos → text, by the AI reading the page image. The AI copies the text
/// word for word (no fixing, no translating); a few pages per call keep answers complete.
/// </summary>
public sealed class ScanTranscriber(IAiClient ai)
{
    public const int PagesPerCall = 5;

    const string Instructions = """
        Bạn chép lại NGUYÊN VĂN văn bản trong các trang được gửi kèm (bản scan hoặc ảnh chụp).
        Quy tắc:
        - Giữ đúng từng chữ, số, dấu câu và dấu tiếng Việt. Không sửa chính tả, không tóm tắt, không dịch, không bình luận.
        - Giữ xuống dòng theo văn bản.
        - Bảng: mỗi hàng một dòng, các ô cách nhau bằng " | ".
        - Chữ viết tay, số đến, chữ ký, con dấu: ghi trong ngoặc vuông, ví dụ [viết tay: 4382], [con dấu: TÊN CƠ QUAN], [chữ ký].
        - Văn bản song ngữ: chép cả hai ngôn ngữ theo đúng thứ tự.
        - Chỗ không đọc được: ghi [không đọc được]. Trang trắng: để text rỗng.
        Trả về đúng một phần tử trong "pages" cho mỗi trang, theo thứ tự trang.
        """;

    static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("pages"),
        ["properties"] = new JsonObject
        {
            ["pages"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("text"),
                    ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
                },
            },
        },
    };

    /// <summary>Reads the given pages (1-based) of a PDF.</summary>
    /// <param name="onCall">called after each AI call with (pages done, pages total)</param>
    public async Task<IReadOnlyList<SourcePage>> TranscribePdfPagesAsync(
        byte[] pdf, IReadOnlyList<int> pageNumbers, Func<int, int, Task>? onCall = null, CancellationToken ct = default)
    {
        var result = new List<SourcePage>();
        foreach (var chunk in pageNumbers.Chunk(PagesPerCall))
        {
            var part = PdfReader.ExtractPages(pdf, chunk);
            var label = chunk.Length == 1 ? $"trang {chunk[0]}" : $"các trang {chunk[0]}–{chunk[^1]}";
            var texts = await AskAsync(new AiFile("pages.pdf", "application/pdf", part), chunk.Length, label, ct);
            result.AddRange(chunk.Select((n, i) => new SourcePage(n, ToBlocks(texts[i]), ReadByAi: true)));
            if (onCall is not null) await onCall(result.Count, pageNumbers.Count);
        }
        return result;
    }

    public async Task<SourcePage> TranscribeImageAsync(byte[] image, string contentType, CancellationToken ct = default)
    {
        var texts = await AskAsync(new AiFile("page", contentType, image), 1, "ảnh", ct);
        return new SourcePage(1, ToBlocks(texts[0]), ReadByAi: true);
    }

    /// <returns>exactly <paramref name="pageCount"/> texts</returns>
    async Task<string[]> AskAsync(AiFile file, int pageCount, string label, CancellationToken ct)
    {
        var answer = await ai.GenerateJsonAsync(new AiRequest(
            Purpose: "transcribe-scan",
            Instructions: Instructions,
            Input: $"File gửi kèm có {pageCount} trang ({label}). Chép lại từng trang.",
            SchemaName: "transcript",
            Schema: Schema,
            Files: [file]), ct);
        var texts = (answer["pages"] as JsonArray ?? [])
            .Select(p => p?["text"]?.GetValue<string>() ?? "")
            .ToList();
        // The model split or merged pages: keep every line, attached to the closest page.
        while (texts.Count > pageCount) { texts[pageCount - 1] += "\n" + texts[pageCount]; texts.RemoveAt(pageCount); }
        while (texts.Count < pageCount) texts.Add("");
        return [.. texts];
    }

    /// <summary>Lines with " | " in a row form a table; other non-empty lines are paragraphs.</summary>
    public static List<SourceBlock> ToBlocks(string text)
    {
        var blocks = new List<SourceBlock>();
        List<IReadOnlyList<SourceCell>>? table = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = SourceText.Clean(raw);
            if (line.Contains(" | ", StringComparison.Ordinal))
            {
                if (table is null) blocks.Add(SourceBlock.Table(table = []));
                table.Add(line.Split('|').Select(c => new SourceCell(c.Trim())).ToList());
                continue;
            }
            table = null;
            if (line.Length > 0) blocks.Add(SourceBlock.Paragraph(line));
        }
        return blocks;
    }
}
