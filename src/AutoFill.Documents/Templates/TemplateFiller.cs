using AutoFill.Documents.Templates.Excel;
using AutoFill.Documents.Templates.Word;

namespace AutoFill.Documents.Templates;

public static class TemplateFiller
{
    /// <summary>The template kind for a file extension; null when the file cannot be a template.</summary>
    public static TemplateKind? KindOf(string ext) => ext switch
    {
        "docx" or "doc" => TemplateKind.Word,
        "xlsx" or "xls" => TemplateKind.Excel,
        _ => null,
    };

    /// <returns>a filled copy; the template bytes are never changed</returns>
    public static byte[] Fill(byte[] template, TemplateKind kind, IReadOnlyList<Slot> slots, IReadOnlyDictionary<string, SlotValue> values, bool highlight) =>
        kind == TemplateKind.Word
            ? WordWriter.Write(template, slots, values, highlight)
            : ExcelWriter.Write(template, slots, values, highlight);
}
