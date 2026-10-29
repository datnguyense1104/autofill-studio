using AutoFill.Documents.Ingest;
using AutoFill.Documents.Templates;

namespace AutoFill.Documents.Runs;

/// <summary>A source document of a run, with the file it came from (evidence points back to it).</summary>
public sealed record RunSource(Guid FileId, string Name, SourceDocument Document);

/// <summary>A check the recipe adds on top of the built-in ones.</summary>
/// <param name="Type">"dateAfter" (Slot's date after Other's), "sameSentenceCount" (Slot and Other), "regex" (Slot matches Pattern)</param>
public sealed record RecipeRule(string Type, string Slot, string? Other = null, string? Pattern = null, string? Message = null);

/// <param name="RowLabels">for each rows slot, the label text of each row (the question, the field name): what the AI answers and what a lookup or a translation keys on</param>
/// <param name="Notes">the recipe's own instructions for every AI call</param>
public sealed record RunInput(
    IReadOnlyList<Slot> Slots,
    IReadOnlyList<RunSource> Sources,
    IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> RowLabels,
    IReadOnlyList<RecipeRule> Rules,
    string? Notes = null,
    bool CheckMissing = true);

public sealed record RunOptions(int BatchSize = 10, int RowsPerCall = 40, int Parallelism = 3, int MaxSourceChars = 120_000, double ReviewBelow = 0.6);

/// <param name="Source">index into <see cref="RunInput.Sources"/></param>
/// <param name="Page">1-based page (sheet number for a spreadsheet)</param>
/// <param name="Found">the quote was found in the source (when false the value has no proof)</param>
public sealed record Evidence(int Source, int Page, string Quote, bool Found);

public enum ValueOrigin { Ai, Rule, None }

/// <summary>One slot's answer: the value, why we believe it, and what the reviewer should look at.</summary>
public sealed record SlotResult(SlotValue Value, ValueOrigin Origin)
{
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
    public double Confidence { get; init; } = 1;
    public string? Note { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool NeedsReview => Warnings.Count > 0;
}

/// <summary>Something important in the sources that no slot carries (asked of the AI after filling).</summary>
/// <param name="Slot">the slot the AI would add it to, if any fits</param>
public sealed record MissingItem(string Item, int Source, int Page, string Quote, string? Slot = null);

public sealed record RunResult(IReadOnlyDictionary<string, SlotResult> Values, IReadOnlyList<MissingItem> Missing)
{
    public IReadOnlyList<string> Notes { get; init; } = [];
}
