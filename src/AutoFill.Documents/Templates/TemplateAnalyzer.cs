using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Documents.Ingest;
using AutoFill.Documents.Templates.Excel;
using AutoFill.Documents.Templates.Word;
using static AutoFill.Documents.Templates.SchemaBuilder;

namespace AutoFill.Documents.Templates;

/// <param name="Template">.docx or .xlsx bytes (old .doc/.xls are converted before)</param>
/// <param name="FilledCopies">other copies of the same template filled for other cases: their differences are near-certain slots</param>
/// <param name="Examples">source documents of the case the template was filled for: what was taken from them is a slot</param>
/// <param name="Notes">the user's own explanation of the template</param>
public sealed record TemplateInput(byte[] Template, TemplateKind Kind, string Name,
    IReadOnlyList<byte[]> FilledCopies, IReadOnlyList<SourceDocument> Examples, string? Notes = null);

/// <summary>
/// Finds the slots of an unmarked template: the AI reads the template (with stable addresses)
/// and says what changes from case to case; code places those picks exactly (see the placers).
/// Nothing here is specific to one customer: the user's notes carry the business rules.
/// </summary>
public sealed class TemplateAnalyzer(IAiClient ai)
{
    public const int MaxExampleChars = 30_000;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    const string Common = """
        valueType: text (ngắn) | longtext (đoạn dài) | number | percent | date | boolean (Có/Không, Y/N, x/trống: ghi các giá trị vào options) | enum (một trong options).
        strategy: extract (chép/lấy từ tài liệu nguồn) | generate (viết mới theo nguồn) | translate (dịch từ chỗ khác) | lookup (tra bảng có sẵn) | constant (lần nào cũng như nhau) | formula (tính từ giá trị khác).
        Số, mã số, ngày, Y/N lấy từ nguồn là extract hoặc lookup, không bao giờ generate.
        label: tên ngắn bằng tiếng Việt. instructions: cách lấy và cách viết giá trị (định dạng ngày, viết hoa, đơn vị...), ngắn gọn.
        group: nhóm thông tin để điền cùng nhau (ví dụ "Người nộp đơn", "Văn bản đến", "Thông tin chung").
        confidence: 0..1, mức chắc chắn đây là chỗ cần điền.
        options: [] khi không phải boolean/enum.
        useWhen: 1–2 câu, mẫu này dùng khi nhận được loại tài liệu nguồn nào.
        """;

    const string WordInstructions = $"""
        Bạn phân tích một file mẫu Word dùng để soạn văn bản lặp lại. File có thể là bản đã điền cho MỘT lần cụ thể.
        Nhiệm vụ: tìm mọi chỗ thay đổi theo từng lần soạn.
        - phrases: cụm chữ là thông tin riêng của lần này: tên người, giới tính, ngày sinh, số giấy tờ, địa chỉ, quốc tịch, tên nước, cơ quan liên quan, số hiệu và ngày văn bản, ngày giờ, địa điểm, số lượng, số tiền...
        - sections: phần thân viết lại toàn bộ theo nguồn (các đoạn văn nêu nội dung riêng của vụ việc), từ đoạn P(from) đến P(to), chỉ gồm đoạn ở thân văn bản (không trong bảng).
        Không chọn: quốc hiệu, tiêu ngữ, tên và địa chỉ của đơn vị soạn văn bản, tiêu đề cố định, câu chữ chung của mẫu, chỗ ngày ký để trống.
        Quy tắc phrases:
        - paragraph: số sau chữ P của đoạn. text: chép NGUYÊN VĂN từng ký tự (cả dấu, khoảng trắng) cụm đó từ đoạn.
        - Lấy cụm liền mạch, đủ nghĩa, ngắn nhất có thể (ví dụ cả "14 giờ, ngày 24/9/2026"), không lấy cả câu khi chỉ một phần là thông tin riêng.
        - Một đoạn có thể có nhiều cụm: mỗi cụm một phần tử. Cụm lặp lại ở nhiều đoạn chỉ cần ghi một lần.
        - Dòng của một danh sách (mỗi dòng một người/một mục, bắt đầu bằng "1.", "-"...) có thông tin riêng: lấy CẢ DÒNG, để danh sách thêm bớt được.
        - Đoạn nằm trong sections thì không cần phrases.
        {Common}
        """;

    const string ExcelInstructions = $"""
        Bạn phân tích một file mẫu Excel dùng để điền lặp lại (biểu mẫu, bảng câu hỏi, bảng dịch...). File có thể là bản đã điền cho MỘT lần cụ thể.
        Mỗi dòng "R5: A5=... | B5=..." là các ô có nội dung của dòng 5. Ô trống không hiện ra: dựa vào nhãn và tiêu đề cột để biết địa chỉ ô cần điền.
        - cells: ô lẻ cần điền (thông tin chung: tên khách hàng, kỳ, người thực hiện, ngày...). cell: địa chỉ như "B3".
        - rowRules: bảng có nhiều dòng cùng kiểu (mỗi dòng một câu hỏi/một mục, cần điền cùng các cột): labelColumn = cột chứa nhãn/câu hỏi, firstRow..lastRow = vùng dòng, targets = các cột cần điền ở mỗi dòng có nhãn (ví dụ cột nhận xét, cột Y/N, cột bản dịch). Dùng rowRules thay vì liệt kê từng ô.
        Không chọn ô có [công thức], ô tiêu đề, ô nhãn cố định.
        {Common}
        """;

    static JsonObject Pick(params (string, JsonObject)[] location) => Obj([
        .. location,
        ("label", Str()), ("valueType", Enum(Names<SlotValueType>())), ("strategy", Enum(Names<SlotStrategy>())),
        ("instructions", Str()), ("options", Arr(Str())), ("group", Str()), ("confidence", Num()),
    ]);

    static readonly JsonObject WordSchema = Obj(
        ("useWhen", Str()),
        ("phrases", Arr(Pick(("paragraph", Int()), ("text", Str())))),
        ("sections", Arr(Obj(("from", Int()), ("to", Int()), ("label", Str()), ("instructions", Str()), ("group", Str()), ("confidence", Num())))));

    static readonly JsonObject ExcelSchema = Obj(
        ("useWhen", Str()),
        ("cells", Arr(Pick(("sheet", Str()), ("cell", Str())))),
        ("rowRules", Arr(Obj(
            ("sheet", Str()), ("labelColumn", Str()), ("firstRow", Int()), ("lastRow", Int()), ("group", Str()), ("confidence", Num()),
            ("targets", Arr(Obj(("column", Str()), ("label", Str()), ("valueType", Enum(Names<SlotValueType>())),
                ("strategy", Enum(Names<SlotStrategy>())), ("instructions", Str()), ("options", Arr(Str())))))))));

    public async Task<TemplateAnalysis> AnalyzeAsync(TemplateInput input, CancellationToken ct = default) =>
        input.Kind == TemplateKind.Word ? await AnalyzeWordAsync(input, ct) : await AnalyzeExcelAsync(input, ct);

    async Task<TemplateAnalysis> AnalyzeWordAsync(TemplateInput input, CancellationToken ct)
    {
        var outline = WordOutline.Read(input.Template);
        if (outline.All(p => p.Kind == ParagraphKind.Empty)) throw new UserFacingException("File mẫu Word không có chữ nào");
        var (phraseDiffs, blockDiffs) = TemplateDiff.Word(outline, input.FilledCopies.Select(WordOutline.Read));

        var hints = new StringBuilder();
        foreach (var d in phraseDiffs) hints.Append($"P{d.Paragraph}: \"{d.Text}\"\n");
        foreach (var b in blockDiffs) hints.Append($"P{b.From}–P{b.To}: cả vùng khác nhau (có thể là phần thân)\n");

        var answer = await AskAsync<WordAnswer>(input, WordInstructions, "word_template", WordSchema,
            WordOutline.Render(outline), hints.ToString(), ct);
        var (slots, notes) = WordSlotPlacer.Place(outline, answer, phraseDiffs, blockDiffs);
        return Result(TemplateKind.Word, answer.UseWhen, slots, notes);
    }

    async Task<TemplateAnalysis> AnalyzeExcelAsync(TemplateInput input, CancellationToken ct)
    {
        var sheets = ExcelOutline.Read(input.Template);
        if (sheets.All(s => s.Cells.Count == 0)) throw new UserFacingException("File mẫu Excel không có ô nào có nội dung");
        var diffs = TemplateDiff.Excel(sheets, input.FilledCopies.Select(ExcelOutline.Read));
        var hints = string.Join("\n", diffs.Select(d => $"{d.Sheet}!{d.Cell}"));

        var answer = await AskAsync<ExcelAnswer>(input, ExcelInstructions, "excel_template", ExcelSchema,
            ExcelOutline.Render(sheets), hints, ct);
        var (slots, notes) = ExcelSlotPlacer.Place(sheets, answer, diffs);
        return Result(TemplateKind.Excel, answer.UseWhen, slots, notes);
    }

    static TemplateAnalysis Result(TemplateKind kind, string useWhen, List<Slot> slots, List<string> notes)
    {
        if (slots.Count == 0) notes.Insert(0, "AI chưa tìm được chỗ cần điền nào, hãy tự thêm trong bước chỉnh công thức");
        return new TemplateAnalysis(kind, slots) { UseWhen = useWhen, Notes = notes };
    }

    async Task<T> AskAsync<T>(TemplateInput input, string instructions, string schemaName, JsonObject schema, string outline, string hints, CancellationToken ct)
    {
        var prompt = new StringBuilder();
        prompt.Append($"# File mẫu \"{input.Name}\"\n{outline}\n");
        if (hints.Length > 0)
            prompt.Append($"\n# Những chỗ khác nhau giữa các bản đã điền của mẫu này (gần như chắc chắn là chỗ cần điền, hãy đặt tên cho chúng)\n{hints}\n");
        if (!string.IsNullOrWhiteSpace(input.Notes)) prompt.Append($"\n# Ghi chú của người dùng về mẫu\n{input.Notes.Trim()}\n");
        if (input.Examples.Count > 0)
        {
            prompt.Append("\n# Tài liệu nguồn của lần đã điền trong mẫu (mọi thông tin trong mẫu lấy hoặc suy ra từ đây đều là chỗ cần điền)\n");
            var left = MaxExampleChars;
            foreach (var doc in input.Examples)
            {
                if (left <= 0) break;
                var (text, _) = SourceText.Render(doc, left);
                prompt.Append(text);
                left -= text.Length;
            }
        }
        var node = await ai.GenerateJsonAsync(new AiRequest("template-analysis", instructions, prompt.ToString(), schemaName, schema), ct);
        return node.Deserialize<T>(Json) ?? throw new InvalidOperationException("AI answer did not match the schema");
    }
}
