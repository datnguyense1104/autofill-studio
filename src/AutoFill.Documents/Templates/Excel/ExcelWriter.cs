using System.Globalization;
using AutoFill.Core;
using ClosedXML.Excel;

namespace AutoFill.Documents.Templates.Excel;

/// <summary>
/// Writes slot values into a copy of an Excel template: only the target cells change, their
/// style stays, numbers and dates are written as numbers and dates (not text), formulas are never
/// overwritten.
/// </summary>
public static class ExcelWriter
{
    static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    static readonly string[] DateFormats = ["d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "d-M-yyyy", "d.M.yyyy"];

    /// <param name="highlight">fill written cells in light yellow so the reviewer sees what changed</param>
    public static byte[] Write(byte[] template, IReadOnlyList<Slot> slots, IReadOnlyDictionary<string, SlotValue> values, bool highlight = false)
    {
        using var book = ExcelOutline.Open(template);
        foreach (var slot in slots)
        {
            if (!values.TryGetValue(slot.Id, out var value)) continue;
            foreach (var location in slot.Locations)
            {
                switch (location)
                {
                    case CellLocation at when value.Text is not null:
                        Set(Cell(book, at.Sheet, at.Cell, slot), slot, value.Text, highlight);
                        break;
                    case RowsLocation at when value.Rows is not null:
                        foreach (var (row, text) in value.Rows)
                            if (at.Rows.Contains(row)) Set(Cell(book, at.Sheet, $"{at.Column}{row}", slot), slot, text, highlight);
                        break;
                }
            }
        }
        using var ms = new MemoryStream();
        book.SaveAs(ms);
        return ms.ToArray();
    }

    static IXLCell Cell(XLWorkbook book, string sheet, string address, Slot slot)
    {
        if (!book.TryGetWorksheet(sheet, out var ws)) throw new UserFacingException($"Ô \"{slot.Label}\" trỏ tới sheet \"{sheet}\" không có trong file mẫu");
        var cell = ws.Cell(address);
        // A merged block keeps its value in its top-left cell.
        return cell.MergedRange()?.FirstCell() ?? cell;
    }

    static void Set(IXLCell cell, Slot slot, string text, bool highlight)
    {
        if (cell.HasFormula) throw new UserFacingException($"Ô {cell.Address} (\"{slot.Label}\") có công thức, không ghi đè");
        text = text.Trim();
        switch (slot.ValueType)
        {
            case SlotValueType.Number when ParseNumber(text) is { } n:
                cell.Value = n;
                break;
            case SlotValueType.Percent when ParseNumber(text.TrimEnd('%').Trim()) is { } p:
                cell.Value = text.EndsWith('%') ? p / 100 : p;
                break;
            case SlotValueType.Date when DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d):
                cell.Value = d;
                if (!cell.Style.DateFormat.Format.Contains('y', StringComparison.OrdinalIgnoreCase)) cell.Style.DateFormat.Format = "dd/MM/yyyy";
                break;
            default:
                // Text that only looks like a number ("0.05", "007") stays exactly as written.
                cell.SetValue(text);
                break;
        }
        if (highlight) cell.Style.Fill.BackgroundColor = XLColor.LightYellow;
    }

    /// <summary>"1234.5" or Vietnamese "1.234,5"; null when it is not a number.</summary>
    public static double? ParseNumber(string text)
    {
        text = text.Replace(" ", "").Replace(" ", "");
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
        if (double.TryParse(text, NumberStyles.Number, Vi, out n)) return n;
        return null;
    }
}
