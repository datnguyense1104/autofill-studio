using System.Text;
using AutoFill.Core;
using ClosedXML.Excel;

namespace AutoFill.Documents.Templates.Excel;

public sealed record OutlineCell(string Address, int Row, string Column, string Text, bool HasFormula);

public sealed record OutlineSheet(string Name, IReadOnlyList<OutlineCell> Cells)
{
    public OutlineCell? Find(string address) => Cells.FirstOrDefault(c => string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Cells that hold something (value or formula), sheet by sheet, with their addresses.</summary>
public static class ExcelOutline
{
    public static XLWorkbook Open(byte[] template)
    {
        try
        {
            return new XLWorkbook(new MemoryStream(template));
        }
        catch (Exception err) when (err is not OutOfMemoryException)
        {
            throw new UserFacingException("File mẫu Excel bị hỏng hoặc không đúng định dạng .xlsx");
        }
    }

    public static IReadOnlyList<OutlineSheet> Read(byte[] template)
    {
        using var book = Open(template);
        return book.Worksheets.Select(ws => new OutlineSheet(ws.Name, ws.CellsUsed(XLCellsUsedOptions.Contents)
            .Select(c => new OutlineCell(c.Address.ToString()!, c.Address.RowNumber, c.Address.ColumnLetter, Text(c), c.HasFormula))
            .Where(c => c.Text.Length > 0 || c.HasFormula)
            .ToList())).ToList();
    }

    static string Text(IXLCell cell)
    {
        try
        {
            return Ingest.SourceText.Clean(cell.GetFormattedString());
        }
        catch (Exception)
        {
            return Ingest.SourceText.Clean(cell.CachedValue.ToString());
        }
    }

    /// <summary>
    /// "## Sheet: EET" then one line per row, "R5: A5=Tên khách hàng | C5=[công thức] 12": every
    /// cell keeps its address. Long texts are shortened so a big sheet still fits the prompt.
    /// </summary>
    public static string Render(IReadOnlyList<OutlineSheet> sheets, int maxChars = 60_000)
    {
        foreach (var cellChars in new[] { 120, 50, 25 })
        {
            var text = RenderWith(sheets, cellChars);
            if (text.Length <= maxChars) return text;
        }
        return RenderWith(sheets, 25)[..maxChars] + "\n…(cắt bớt, sheet quá lớn)";
    }

    static string RenderWith(IReadOnlyList<OutlineSheet> sheets, int cellChars)
    {
        var sb = new StringBuilder();
        foreach (var sheet in sheets)
        {
            sb.Append($"## Sheet: {sheet.Name}\n");
            foreach (var row in sheet.Cells.GroupBy(c => c.Row).OrderBy(g => g.Key))
            {
                sb.Append($"R{row.Key}: ");
                sb.Append(string.Join(" | ", row.Select(c =>
                {
                    var text = c.Text.Replace("\n", " ");
                    if (text.Length > cellChars) text = text[..cellChars] + "…";
                    return $"{c.Address}={(c.HasFormula ? "[công thức] " : "")}{text}";
                })));
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}
