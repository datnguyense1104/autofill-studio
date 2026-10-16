using System.Text.RegularExpressions;

namespace AutoFill.Documents.Templates.Word;

/// <summary>A phrase the AI picked: paragraph number and the words copied from it.</summary>
public sealed record WordPhrasePick(int Paragraph, string Text, string Label, string ValueType, string Strategy,
    string Instructions, IReadOnlyList<string> Options, string Group, double Confidence);

public sealed record WordSectionPick(int From, int To, string Label, string Instructions, string Group, double Confidence);

public sealed record WordAnswer(string UseWhen, IReadOnlyList<WordPhrasePick> Phrases, IReadOnlyList<WordSectionPick> Sections);

/// <summary>
/// Turns the AI's picks into slots with exact locations. The AI only copies words (it is bad at
/// counting characters); code finds them, adds the places where the same words repeat, marks
/// list entries whole, drops overlaps, and adds what the filled copies show changes but the AI missed.
/// </summary>
public static class WordSlotPlacer
{
    const string DiffLabel = "Chỗ khác nhau giữa các bản đã điền";

    sealed class Draft
    {
        public required string Label;
        public string Instructions = "";
        public SlotValueType ValueType;
        public SlotStrategy Strategy;
        public IReadOnlyList<string> Options = [];
        public string Group = "";
        public double Confidence;
        public string? Sample;
        public List<SlotLocation> Locations = [];
    }

    public static (List<Slot> Slots, List<string> Notes) Place(
        IReadOnlyList<OutlineParagraph> outline, WordAnswer answer, IReadOnlyList<PhraseDiff> phraseDiffs, IReadOnlyList<BlockDiff> blockDiffs)
    {
        var notes = new List<string>();
        var drafts = new List<Draft>();
        var sections = new List<SectionLocation>();
        bool InSection(int paragraph) => sections.Any(s => paragraph >= s.From && paragraph <= s.To);

        void AddSection(int from, int to, Draft draft)
        {
            var range = outline.Where(p => p.Index >= from && p.Index <= to).ToList();
            if (from > to || range.Count == 0)
            {
                notes.Add($"Bỏ qua phần thân P{from}–P{to}: không có trong mẫu");
                return;
            }
            if (range.Any(p => !p.TopLevel))
            {
                notes.Add($"Bỏ qua phần thân P{from}–P{to}: có bảng hoặc nằm ngoài thân văn bản, hãy chọn lại vùng");
                return;
            }
            var filled = range.Where(p => p.Kind != ParagraphKind.Empty).ToList();
            if (filled.Count == 0 || sections.Any(s => s.From <= filled[^1].Index && filled[0].Index <= s.To)) return;
            var at = new SectionLocation(filled[0].Index, filled[^1].Index);
            sections.Add(at);
            draft.Sample = string.Join("\n", filled.Select(p => p.Text.Trim()));
            draft.Locations.Add(at);
            drafts.Add(draft);
        }

        foreach (var s in answer.Sections)
        {
            AddSection(s.From, s.To, new Draft
            {
                Label = s.Label, Instructions = s.Instructions, Group = s.Group, Confidence = s.Confidence,
                ValueType = SlotValueType.LongText, Strategy = SlotStrategy.Generate,
            });
        }
        foreach (var b in blockDiffs.Where(b => !sections.Any(s => s.From <= b.To && b.From <= s.To)))
        {
            AddSection(b.From, b.To, new Draft
            {
                Label = "Phần thân (khác nhau giữa các bản)", Confidence = 0.4,
                ValueType = SlotValueType.LongText, Strategy = SlotStrategy.Generate,
            });
        }

        bool AddPhrase(int paragraph, string text, Draft draft, bool repeats)
        {
            text = text.Trim();
            if (text.Length == 0) return false;
            var found = Locate(outline, paragraph, text);
            if (found is null) return false;
            var (p, start, length) = found.Value;
            if (InSection(p.Index)) return true;
            // A list line taken from its first word to its end (with or without the "1. " mark) is the whole entry.
            var lead = p.Text.Length - p.Text.TrimStart().Length;
            var end = lead + p.Text.Trim().Length;
            var mark = p.Kind is ParagraphKind.Bullet or ParagraphKind.Numbered ? p.Text.Length - WordParagraphs.StripListMark(p.Text).Length - lead : 0;
            var whole = p.Kind is ParagraphKind.Bullet or ParagraphKind.Numbered or ParagraphKind.List && start <= lead + mark && start + length == end;
            if (whole) (start, length) = (lead, end - lead);
            draft.Locations.Add(new PhraseLocation(p.Index, start, length, whole));
            draft.Sample ??= p.Text.Substring(start, length);
            if (repeats && !whole && text.Length >= 4)
            {
                foreach (var other in outline.Where(o => !InSection(o.Index) && o.Kind != ParagraphKind.Empty))
                {
                    foreach (var (s, l) in Occurrences(other.Text, text))
                        if (!(other.Index == p.Index && s == start)) draft.Locations.Add(new PhraseLocation(other.Index, s, l));
                }
            }
            drafts.Add(draft);
            return true;
        }

        foreach (var pick in answer.Phrases)
        {
            var draft = new Draft
            {
                Label = pick.Label, Instructions = pick.Instructions, Group = pick.Group, Confidence = pick.Confidence, Options = pick.Options,
                ValueType = SchemaBuilder.Parse(pick.ValueType, SlotValueType.Text), Strategy = SchemaBuilder.Parse(pick.Strategy, SlotStrategy.Extract),
            };
            if (!AddPhrase(pick.Paragraph, pick.Text, draft, repeats: true))
                notes.Add($"Không tìm thấy cụm \"{Short(pick.Text)}\" (P{pick.Paragraph}) mà AI chọn, hãy tự thêm nếu cần");
        }
        foreach (var d in phraseDiffs)
        {
            var covered = drafts.SelectMany(x => x.Locations).OfType<PhraseLocation>()
                .Any(l => l.Paragraph == d.Paragraph && outline[d.Paragraph].Text.IndexOf(d.Text, StringComparison.Ordinal) is var i && i < l.Start + l.Length && l.Start < i + d.Text.Length);
            if (!covered) AddPhrase(d.Paragraph, d.Text, new Draft { Label = DiffLabel, Confidence = 0.4 }, repeats: false);
        }

        DropOverlaps(drafts, notes);
        var slots = drafts.Where(d => d.Locations.Count > 0)
            .OrderBy(d => First(d).Paragraph).ThenBy(d => First(d).Start)
            .Select((d, i) => new Slot
            {
                Id = $"s{i + 1}", Label = d.Label, Instructions = d.Instructions, Locations = d.Locations, ValueType = d.ValueType,
                Strategy = d.Strategy, Options = d.Options, Group = d.Group, SampleValue = d.Sample, Confidence = Math.Clamp(d.Confidence, 0, 1),
            })
            .ToList();
        return (slots, notes);
    }

    static (int Paragraph, int Start) First(Draft d) => d.Locations[0] switch
    {
        PhraseLocation p => (p.Paragraph, p.Start),
        SectionLocation s => (s.From, -1),
        _ => (int.MaxValue, 0),
    };

    /// <summary>Two slots never write the same characters: the longer phrase wins.</summary>
    static void DropOverlaps(List<Draft> drafts, List<string> notes)
    {
        var kept = new List<PhraseLocation>();
        var all = drafts.SelectMany(d => d.Locations.OfType<PhraseLocation>().Select(l => (Draft: d, At: l)))
            .OrderByDescending(x => x.At.Length).ToList();
        foreach (var (draft, at) in all)
        {
            if (kept.Any(k => k.Paragraph == at.Paragraph && k.Start < at.Start + at.Length && at.Start < k.Start + k.Length))
                draft.Locations.Remove(at);
            else kept.Add(at);
        }
        foreach (var d in drafts.Where(d => d.Locations.Count == 0 && d.Label != DiffLabel))
            notes.Add($"Bỏ \"{d.Label}\": trùng với một chỗ cần điền dài hơn");
    }

    /// <summary>
    /// Where the AI's words are: first in the paragraph it named, then anywhere (it sometimes
    /// miscounts). Matching uses the text as shown to the AI, then tolerates different spacing.
    /// </summary>
    static (OutlineParagraph P, int Start, int Length)? Locate(IReadOnlyList<OutlineParagraph> outline, int paragraph, string text)
    {
        var order = outline.Where(p => p.Index == paragraph).Concat(outline.Where(p => p.Index != paragraph));
        foreach (var p in order)
        {
            if (p.Kind == ParagraphKind.Empty) continue;
            var hit = Occurrences(p.Text, text).Cast<(int, int)?>().FirstOrDefault();
            if (hit is { } h) return (p, h.Item1, h.Item2);
        }
        return null;
    }

    /// <summary>Every place <paramref name="phrase"/> (as the AI wrote it) occurs in a paragraph, as real-text offsets.</summary>
    static IEnumerable<(int Start, int Length)> Occurrences(string paragraphText, string phrase)
    {
        var (shown, map) = WordOutline.Display(paragraphText);
        var pattern = string.Join(@"\s+", phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        if (pattern.Length == 0) yield break;
        foreach (Match m in Regex.Matches(shown, pattern))
        {
            var start = map[m.Index];
            var end = map[m.Index + m.Length];
            if (end > start) yield return (start, end - start);
        }
    }

    static string Short(string text) => text.Length > 60 ? text[..60] + "…" : text;
}
