using System.Text.RegularExpressions;

namespace AutoFill.Documents.Templates.Excel;

public sealed record ExcelCellPick(string Sheet, string Cell, string Label, string ValueType, string Strategy,
    string Instructions, IReadOnlyList<string> Options, string Group, double Confidence);

public sealed record ExcelTargetPick(string Column, string Label, string ValueType, string Strategy, string Instructions, IReadOnlyList<string> Options);

/// <summary>"Every row of <see cref="LabelColumn"/> between the two rows that has a label gets these columns filled."</summary>
public sealed record ExcelRowRulePick(string Sheet, string LabelColumn, int FirstRow, int LastRow, string Group, double Confidence, IReadOnlyList<ExcelTargetPick> Targets);

public sealed record ExcelAnswer(string UseWhen, IReadOnlyList<ExcelCellPick> Cells, IReadOnlyList<ExcelRowRulePick> RowRules);

/// <summary>
/// Turns the AI's picks into slots: checks every address, never targets a formula, and expands a
/// row rule into the actual rows that carry a label (so a 255-question sheet is 3 slots, not 765).
/// </summary>
public static partial class ExcelSlotPlacer
{
    [GeneratedRegex("^[A-Z]{1,3}[1-9][0-9]{0,6}$")]
    private static partial Regex Address();

    [GeneratedRegex("^[A-Z]{1,3}$")]
    private static partial Regex Column();

    public static (List<Slot> Slots, List<string> Notes) Place(IReadOnlyList<OutlineSheet> sheets, ExcelAnswer answer, IReadOnlyList<CellDiff> diffs)
    {
        var notes = new List<string>();
        var slots = new List<Slot>();
        var taken = new HashSet<(string Sheet, string Cell)>();
        OutlineSheet? SheetOf(string name) => sheets.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        void AddCell(ExcelCellPick pick)
        {
            var cell = pick.Cell.Trim().TrimStart('$').Replace("$", "").ToUpperInvariant();
            if (SheetOf(pick.Sheet) is not { } sheet) { notes.Add($"Bỏ \"{pick.Label}\": không có sheet \"{pick.Sheet}\""); return; }
            if (!Address().IsMatch(cell)) { notes.Add($"Bỏ \"{pick.Label}\": địa chỉ ô \"{pick.Cell}\" không hợp lệ"); return; }
            if (sheet.Find(cell)?.HasFormula == true) { notes.Add($"Bỏ \"{pick.Label}\": ô {cell} có công thức"); return; }
            if (!taken.Add((sheet.Name, cell))) return;
            slots.Add(new Slot
            {
                Id = "", Label = pick.Label, Instructions = pick.Instructions, Locations = [new CellLocation(sheet.Name, cell)],
                ValueType = SchemaBuilder.Parse(pick.ValueType, SlotValueType.Text), Strategy = SchemaBuilder.Parse(pick.Strategy, SlotStrategy.Extract),
                Options = pick.Options, Group = pick.Group, SampleValue = sheet.Find(cell)?.Text, Confidence = Math.Clamp(pick.Confidence, 0, 1),
            });
        }

        foreach (var pick in answer.Cells) AddCell(pick);

        foreach (var rule in answer.RowRules)
        {
            var labelColumn = rule.LabelColumn.Trim().ToUpperInvariant();
            var where = $"{rule.Sheet}!{labelColumn}{rule.FirstRow}–{labelColumn}{rule.LastRow}";
            if (SheetOf(rule.Sheet) is not { } sheet || !Column().IsMatch(labelColumn) || rule.FirstRow < 1 || rule.LastRow < rule.FirstRow)
            {
                notes.Add($"Bỏ luật dòng {where}: sheet hoặc cột không hợp lệ");
                continue;
            }
            var labelled = sheet.Cells.Where(c => c.Column == labelColumn && c.Row >= rule.FirstRow && c.Row <= rule.LastRow && c.Text.Length > 0 && !c.HasFormula)
                .Select(c => c.Row).Distinct().Order().ToList();
            foreach (var target in rule.Targets)
            {
                var column = target.Column.Trim().ToUpperInvariant();
                if (!Column().IsMatch(column) || column == labelColumn) { notes.Add($"Bỏ cột \"{target.Column}\" của luật dòng {where}"); continue; }
                var rows = labelled.Where(r => sheet.Find($"{column}{r}")?.HasFormula != true && taken.Add((sheet.Name, $"{column}{r}"))).ToList();
                if (rows.Count == 0) { notes.Add($"Luật dòng {where}, cột {column}: không có dòng nào để điền"); continue; }
                slots.Add(new Slot
                {
                    Id = "", Label = target.Label, Instructions = target.Instructions,
                    Locations = [new RowsLocation(sheet.Name, labelColumn, column, rows)],
                    ValueType = SchemaBuilder.Parse(target.ValueType, SlotValueType.Text), Strategy = SchemaBuilder.Parse(target.Strategy, SlotStrategy.Extract),
                    Options = target.Options, Group = rule.Group, Confidence = Math.Clamp(rule.Confidence, 0, 1),
                });
                notes.Add($"Luật dòng {where} → cột {column} \"{target.Label}\": {rows.Count} dòng");
            }
        }

        foreach (var d in diffs.Where(d => !taken.Contains((d.Sheet, d.Cell))))
            AddCell(new ExcelCellPick(d.Sheet, d.Cell, $"Ô {d.Cell} (khác nhau giữa các bản đã điền)", "text", "extract", "", [], "", 0.4));

        return (slots.Select((s, i) => s with { Id = $"s{i + 1}" }).ToList(), notes);
    }
}
