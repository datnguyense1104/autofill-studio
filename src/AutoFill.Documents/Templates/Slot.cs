using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AutoFill.Documents.Templates;

public enum TemplateKind { Word, Excel }

public enum SlotValueType { Text, LongText, Number, Percent, Date, Boolean, Enum }

/// <summary>How a slot gets its value: AI (extract, generate, translate) or rules (lookup, constant, formula).</summary>
public enum SlotStrategy { Extract, Generate, Translate, Lookup, Constant, Formula }

/// <summary>
/// One place to fill in a template. A slot may sit at several locations (a name repeated in
/// the letter) that always take the same value.
/// </summary>
public sealed record Slot
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Instructions { get; init; } = "";
    public required IReadOnlyList<SlotLocation> Locations { get; init; }
    public SlotValueType ValueType { get; init; } = SlotValueType.Text;
    public SlotStrategy Strategy { get; init; } = SlotStrategy.Extract;
    /// <summary>Strategy settings (lookup table, constant value, target language...), set in the recipe builder.</summary>
    public JsonObject? StrategyConfig { get; init; }
    /// <summary>Allowed values for <see cref="SlotValueType.Enum"/> and <see cref="SlotValueType.Boolean"/> (e.g. "Y", "N").</summary>
    public IReadOnlyList<string> Options { get; init; } = [];
    public bool Required { get; init; } = true;
    /// <summary>Slots of a group are filled together (one AI call), e.g. "Người được thăm".</summary>
    public string Group { get; init; } = "";
    /// <summary>The value in the template as uploaded (a filled example), shown as a hint.</summary>
    public string? SampleValue { get; init; }
    /// <summary>0..1, how sure the analysis is that this is a slot; low ones are shown first for review.</summary>
    public double Confidence { get; init; } = 1;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PhraseLocation), "phrase")]
[JsonDerivedType(typeof(SectionLocation), "section")]
[JsonDerivedType(typeof(CellLocation), "cell")]
[JsonDerivedType(typeof(RowsLocation), "rows")]
public abstract record SlotLocation;

/// <summary>
/// Characters [Start, Start+Length) of one Word paragraph (numbering from <see cref="Word.WordParagraphs"/>).
/// <see cref="WholeParagraph"/>: an entry of a list (one person...): an empty value removes the
/// paragraph, several lines become several paragraphs.
/// </summary>
public sealed record PhraseLocation(int Paragraph, int Start, int Length, bool WholeParagraph = false) : SlotLocation;

/// <summary>Body paragraphs [From, To] rewritten as a whole; new paragraphs copy the look of the old ones.</summary>
public sealed record SectionLocation(int From, int To) : SlotLocation;

public sealed record CellLocation(string Sheet, string Cell) : SlotLocation;

/// <summary>One column of a row rule: the cell of <see cref="Column"/> on each of <see cref="Rows"/>; the value is one text per row.</summary>
public sealed record RowsLocation(string Sheet, string LabelColumn, string Column, IReadOnlyList<int> Rows) : SlotLocation;

/// <summary>What the analysis of a template found: its slots and when to use it.</summary>
public sealed record TemplateAnalysis(TemplateKind Kind, IReadOnlyList<Slot> Slots)
{
    /// <summary>"Dùng khi nào": what source documents this template answers, for choosing a recipe automatically.</summary>
    public string UseWhen { get; init; } = "";
    /// <summary>Things the user should check (AI picks that could not be placed, overlaps dropped...).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>A slot's value at write time: <see cref="Text"/>, or for a <see cref="RowsLocation"/> one text per row.</summary>
public sealed record SlotValue(string? Text = null, IReadOnlyDictionary<int, string>? Rows = null);
