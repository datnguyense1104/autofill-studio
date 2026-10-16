using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Documents.Templates;
using AutoFill.Documents.Templates.Excel;
using ClosedXML.Excel;

namespace AutoFill.Tests;

public sealed class TemplateExcelTests
{
    /// <summary>A small B210-like questionnaire: header cells, then questions in B with answer columns G (comment), I (Y/N), L (impact).</summary>
    static byte[] Questionnaire(Action<IXLWorksheet>? fill = null)
    {
        using var book = new XLWorkbook();
        var ws = book.AddWorksheet("B210");
        ws.Cell("A3").Value = "Tên khách hàng";
        ws.Cell("A4").Value = "Kỳ kế toán";
        ws.Cell("K3").Value = "Người thực hiện";
        ws.Cell("B9").Value = "Câu hỏi";
        ws.Cell("G9").Value = "Nhận xét";
        ws.Cell("I9").Value = "Y/N";
        ws.Cell("L9").Value = "Ảnh hưởng";
        for (var r = 10; r <= 14; r++) ws.Cell(r, 2).Value = $"□ Câu hỏi số {r - 9}";
        ws.Cell("I12").FormulaA1 = "\"N/A\"";
        ws.Cell("B3").Style.Font.Bold = true;
        fill?.Invoke(ws);
        using var ms = new MemoryStream();
        book.SaveAs(ms);
        return ms.ToArray();
    }

    static ExcelAnswer Answer() => new("Khi lập B210",
        [
            new ExcelCellPick("B210", "B3", "Tên khách hàng", "text", "extract", "", [], "Chung", 0.9),
            new ExcelCellPick("b210", "$B$4", "Kỳ", "date", "extract", "", [], "Chung", 0.9),
            new ExcelCellPick("B210", "I12", "Có công thức", "text", "extract", "", [], "", 0.9),
            new ExcelCellPick("Không có", "A1", "Sai sheet", "text", "extract", "", [], "", 0.9),
        ],
        [new ExcelRowRulePick("B210", "B", 10, 40, "Đánh giá", 0.8, [
            new ExcelTargetPick("G", "Nhận xét", "longtext", "generate", "", []),
            new ExcelTargetPick("I", "Y/N", "boolean", "extract", "", ["Y", "N"]),
        ])]);

    [Fact]
    public void A_row_rule_covers_the_labelled_rows_and_skips_formulas()
    {
        var (slots, notes) = ExcelSlotPlacer.Place(ExcelOutline.Read(Questionnaire()), Answer(), []);

        Assert.Equal(["Tên khách hàng", "Kỳ", "Nhận xét", "Y/N"], slots.Select(s => s.Label));
        Assert.Equal(new CellLocation("B210", "B4"), slots[1].Locations.Single());
        var g = (RowsLocation)slots[2].Locations.Single();
        Assert.Equal(("B", "G"), (g.LabelColumn, g.Column));
        Assert.Equal([10, 11, 12, 13, 14], g.Rows);
        Assert.Equal([10, 11, 13, 14], ((RowsLocation)slots[3].Locations.Single()).Rows);
        Assert.Equal(["Y", "N"], slots[3].Options);
        Assert.Contains(notes, n => n.Contains("I12 có công thức"));
        Assert.Contains(notes, n => n.Contains("không có sheet"));
    }

    [Fact]
    public void Writing_changes_only_the_target_cells_and_types_numbers_and_dates()
    {
        var template = Questionnaire();
        Slot[] slots =
        [
            new() { Id = "name", Label = "Tên", Locations = [new CellLocation("B210", "B3")] },
            new() { Id = "date", Label = "Kỳ", ValueType = SlotValueType.Date, Locations = [new CellLocation("B210", "B4")] },
            new() { Id = "rate", Label = "Tỷ lệ", ValueType = SlotValueType.Percent, Locations = [new CellLocation("B210", "C4")] },
            new() { Id = "amount", Label = "Số tiền", ValueType = SlotValueType.Number, Locations = [new CellLocation("B210", "C5")] },
            new() { Id = "code", Label = "Mã", Locations = [new CellLocation("B210", "C6")] },
            new() { Id = "yn", Label = "Y/N", ValueType = SlotValueType.Boolean, Locations = [new RowsLocation("B210", "B", "I", [10, 11])] },
        ];
        var values = new Dictionary<string, SlotValue>
        {
            ["name"] = new("Công ty TNHH Ví Dụ"), ["date"] = new("31/12/2025"), ["rate"] = new("12,5%"),
            ["amount"] = new("1.234.567,5"), ["code"] = new("0.05"),
            ["yn"] = new(Rows: new Dictionary<int, string> { [10] = "Y", [11] = "N", [99] = "ngoài luật" }),
        };

        var filled = ExcelWriter.Write(template, slots, values);

        using var before = new XLWorkbook(new MemoryStream(template));
        using var after = new XLWorkbook(new MemoryStream(filled));
        var ws = after.Worksheet("B210");
        Assert.Equal("Công ty TNHH Ví Dụ", ws.Cell("B3").GetString());
        Assert.True(ws.Cell("B3").Style.Font.Bold);
        Assert.Equal(new DateTime(2025, 12, 31), ws.Cell("B4").GetDateTime());
        Assert.Equal(0.125, ws.Cell("C4").GetDouble(), 6);
        Assert.Equal(1234567.5, ws.Cell("C5").GetDouble(), 6);
        Assert.Equal("0.05", ws.Cell("C6").GetString()); // a code that looks like a number stays text
        Assert.Equal(XLDataType.Text, ws.Cell("C6").DataType);
        Assert.Equal(["Y", "N"], new[] { ws.Cell("I10").GetString(), ws.Cell("I11").GetString() });
        Assert.True(ws.Cell("I99").IsEmpty());

        // Every other cell is as it was, formulas included.
        var written = new HashSet<string> { "B3", "B4", "C4", "C5", "C6", "I10", "I11" };
        foreach (var cell in before.Worksheet("B210").CellsUsed(XLCellsUsedOptions.Contents).Where(c => !written.Contains(c.Address.ToString()!)))
            Assert.Equal(cell.Value.ToString(), ws.Cell(cell.Address.ToString()!).Value.ToString());
        Assert.True(ws.Cell("I12").HasFormula);
    }

    [Fact]
    public void A_formula_cell_is_never_overwritten()
    {
        Slot slot = new() { Id = "f", Label = "Công thức", Locations = [new CellLocation("B210", "I12")] };
        Assert.Throws<UserFacingException>(() => ExcelWriter.Write(Questionnaire(), [slot], new Dictionary<string, SlotValue> { ["f"] = new("Y") }));
    }

    [Fact]
    public async Task Cells_that_differ_between_filled_copies_are_slots_and_hints()
    {
        var template = Questionnaire(ws => { ws.Cell("B3").Value = "Công ty A"; ws.Cell("L3").Value = "01/01/2025"; });
        var other = Questionnaire(ws => { ws.Cell("B3").Value = "Công ty B"; ws.Cell("L3").Value = "02/02/2025"; ws.Cell("G10").Value = "Không"; });
        var ai = new FakeAi(_ => new JsonObject
        {
            ["useWhen"] = "B210",
            ["cells"] = new JsonArray(new JsonObject
            {
                ["sheet"] = "B210", ["cell"] = "B3", ["label"] = "Tên khách hàng", ["valueType"] = "text", ["strategy"] = "extract",
                ["instructions"] = "", ["options"] = new JsonArray(), ["group"] = "", ["confidence"] = 0.9,
            }),
            ["rowRules"] = new JsonArray(),
        });

        var analysis = await new TemplateAnalyzer(ai).AnalyzeAsync(new TemplateInput(template, TemplateKind.Excel, "b210.xlsx", [other], []));

        Assert.Equal(["B3", "G10", "L3"], analysis.Slots.Select(s => ((CellLocation)s.Locations.Single()).Cell));
        Assert.Equal("Tên khách hàng", analysis.Slots[0].Label);
        Assert.Equal("Công ty A", analysis.Slots[0].SampleValue);
        var prompt = ai.Requests.Single().Input;
        Assert.Contains("R12: B12=□ Câu hỏi số 3 | I12=[công thức] N/A", prompt);
        Assert.Contains("B210!G10", prompt);
    }
}
