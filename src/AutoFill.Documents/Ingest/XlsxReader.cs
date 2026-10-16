using AutoFill.Core;
using ClosedXML.Excel;

namespace AutoFill.Documents.Ingest;

/// <summary>
/// Excel workbook → one page per sheet, one table per sheet, every cell with its address.
/// Values are the text Excel shows (formulas give their saved result); empty cells are skipped.
/// </summary>
public static class XlsxReader
{
    public static SourceDocument Read(byte[] content, SourceKind kind = SourceKind.Xlsx)
    {
        XLWorkbook book;
        try
        {
            book = new XLWorkbook(new MemoryStream(content));
        }
        catch (Exception err) when (err is not OutOfMemoryException)
        {
            throw new UserFacingException("File Excel bị hỏng hoặc không đúng định dạng .xlsx");
        }
        using (book)
        {
            var pages = new List<SourcePage>();
            foreach (var sheet in book.Worksheets)
            {
                var rows = sheet.CellsUsed(XLCellsUsedOptions.Contents)
                    .Select(c => (Row: c.Address.RowNumber, Cell: new SourceCell(CellText(c), c.Address.ToString())))
                    .Where(c => c.Cell.Text.Length > 0)
                    .GroupBy(c => c.Row)
                    .OrderBy(g => g.Key)
                    .Select(g => (IReadOnlyList<SourceCell>)g.Select(c => c.Cell).ToList())
                    .ToList();
                pages.Add(new SourcePage(pages.Count + 1, rows.Count > 0 ? [SourceBlock.Table(rows)] : [], sheet.Name));
            }
            return new SourceDocument(kind, pages);
        }
    }

    static string CellText(IXLCell cell)
    {
        try
        {
            return SourceText.Clean(cell.GetFormattedString());
        }
        catch (Exception)
        {
            // A formula ClosedXML cannot evaluate and no saved result: fall back to the raw value.
            return SourceText.Clean(cell.CachedValue.ToString());
        }
    }
}
