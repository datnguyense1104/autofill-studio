using AutoFill.Core;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Run = DocumentFormat.OpenXml.Wordprocessing.Run;

namespace AutoFill.Documents.Templates.Word;

/// <summary>
/// Writes slot values into a copy of a Word template. A phrase keeps the formatting of the run
/// it starts in (runs are split at its edges, so the text around it is untouched); a rewritten
/// section copies paragraph and font settings from the template paragraph of the same kind.
/// </summary>
public static class WordWriter
{
    /// <param name="highlight">mark written text in yellow so the reviewer sees what changed</param>
    /// <returns>the filled .docx; slots without a value are left as in the template</returns>
    public static byte[] Write(byte[] template, IReadOnlyList<Slot> slots, IReadOnlyDictionary<string, SlotValue> values, bool highlight = false)
    {
        using var ms = new MemoryStream();
        ms.Write(template);
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var paragraphs = WordParagraphs.Collect(doc);
            var phrases = new List<(WordParagraph P, PhraseLocation At, string Value)>();
            var sections = new List<(SectionLocation At, string Value)>();
            foreach (var slot in slots)
            {
                if (!values.TryGetValue(slot.Id, out var value) || value.Text is null) continue;
                foreach (var location in slot.Locations)
                {
                    switch (location)
                    {
                        case PhraseLocation at:
                            var p = At(paragraphs, at.Paragraph, slot);
                            if (p.Runs.Any(r => r.Locked && r.Start < at.Start + at.Length && r.End > at.Start))
                                throw new UserFacingException($"Ô \"{slot.Label}\" nằm trong trường tự động của Word (ngày, ô nhập...), không ghi được");
                            phrases.Add((p, at, value.Text));
                            break;
                        case SectionLocation at:
                            sections.Add((at, value.Text));
                            break;
                    }
                }
            }

            // Right to left inside a paragraph, so earlier offsets stay valid.
            foreach (var group in phrases.GroupBy(x => x.P))
            {
                foreach (var (p, at, value) in group.OrderByDescending(x => x.At.Start))
                {
                    if (at.WholeParagraph) WriteListEntry(p, value, highlight);
                    else Replace(p.Element, at.Start, at.Length, value.Trim(), highlight);
                }
            }
            foreach (var (at, value) in sections) WriteSection(paragraphs, at, value, highlight);
        }
        return ms.ToArray();
    }

    static WordParagraph At(IReadOnlyList<WordParagraph> paragraphs, int index, Slot slot) =>
        index >= 0 && index < paragraphs.Count ? paragraphs[index]
            : throw new UserFacingException($"Ô \"{slot.Label}\" trỏ tới đoạn không có trong file mẫu, hãy phân tích lại mẫu");

    /// <summary>Replaces characters [start, start+length) of a paragraph with <paramref name="value"/>.</summary>
    public static void Replace(Paragraph p, int start, int length, string value, bool highlight)
    {
        SplitAt(p, start);
        SplitAt(p, start + length);
        var inside = WordParagraphs.Runs(p).Where(r => r.Start >= start && r.End <= start + length).ToList();
        if (inside.Count == 0) throw new InvalidOperationException("Phrase range holds no runs");
        foreach (var r in inside) RemoveText(r.Element);
        AppendText(inside[0].Element, value);
        if (highlight && value.Length > 0) Highlight(inside[0].Element);
    }

    /// <summary>A list entry: empty removes the paragraph, each extra line becomes a copy right after it.</summary>
    static void WriteListEntry(WordParagraph p, string value, bool highlight)
    {
        var lines = value.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
        var lead = p.Text[..(p.Text.Length - p.Text.TrimStart().Length)];
        if (lines.Count == 0)
        {
            // A table cell must keep one paragraph.
            if (p.Element.Parent is TableCell cell && cell.Elements<Paragraph>().Count() == 1) Replace(p.Element, 0, p.Text.Length, "", false);
            else p.Element.Remove();
            return;
        }
        var original = (Paragraph)p.Element.CloneNode(true);
        Replace(p.Element, 0, p.Text.Length, lead + lines[0], highlight);
        OpenXmlElement after = p.Element;
        foreach (var line in lines.Skip(1))
        {
            var copy = (Paragraph)original.CloneNode(true);
            Replace(copy, 0, WordParagraphs.Runs(copy).Sum(r => r.Text.Length), lead + line, highlight);
            after = after.InsertAfterSelf(copy);
        }
    }

    /// <summary>
    /// Replaces body paragraphs [From, To] with one paragraph per line of the value. Each new
    /// paragraph is shaped like the nearest old one of the same kind (prose, "- " item, "1. " item).
    /// </summary>
    static void WriteSection(IReadOnlyList<WordParagraph> paragraphs, SectionLocation at, string value, bool highlight)
    {
        if (at.From < 0 || at.To >= paragraphs.Count || at.From > at.To || !paragraphs[at.From].TopLevel || !paragraphs[at.To].TopLevel)
            throw new UserFacingException("Phần thân trỏ tới vùng không có trong file mẫu, hãy phân tích lại mẫu");
        var old = paragraphs.Skip(at.From).Take(at.To - at.From + 1).ToList();
        var shapes = old.Where(p => p.Kind != ParagraphKind.Empty).ToList();
        if (shapes.Count == 0) shapes = old;

        var lines = value.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
        OpenXmlElement anchor = old[0].Element;
        for (var i = 0; i < lines.Count; i++)
        {
            var kind = WordParagraphs.KindOfLine(lines[i]);
            // Same kind (a Word-numbered item stands in for a hand-typed one), nearest by position.
            var shape = shapes
                .OrderBy(s => s.Kind == kind || (s.Kind == ParagraphKind.List && kind is ParagraphKind.Bullet or ParagraphKind.Numbered) ? 0 : 1)
                .ThenBy(s => Math.Abs(shapes.IndexOf(s) - i * shapes.Count / Math.Max(lines.Count, 1)))
                .First();
            var text = shape.Kind == ParagraphKind.List ? WordParagraphs.StripListMark(lines[i]) : lines[i];
            anchor.InsertBeforeSelf(ParagraphLike(shape, text, highlight));
        }
        foreach (var p in old) p.Element.Remove();
    }

    /// <summary>A new paragraph with the paragraph settings and first-run font of <paramref name="shape"/>.</summary>
    static Paragraph ParagraphLike(WordParagraph shape, string text, bool highlight)
    {
        var p = new Paragraph();
        if (shape.Element.ParagraphProperties is { } pPr) p.Append(pPr.CloneNode(true));
        var run = new Run();
        if (shape.Runs.FirstOrDefault()?.Element.RunProperties is { } rPr) run.Append(rPr.CloneNode(true));
        AppendText(run, text);
        if (highlight) Highlight(run);
        p.Append(run);
        return p;
    }

    /// <summary>Makes a run boundary at character <paramref name="offset"/> of the paragraph (no-op when one is there).</summary>
    static void SplitAt(Paragraph p, int offset)
    {
        var run = WordParagraphs.Runs(p).FirstOrDefault(r => r.Start < offset && r.End > offset);
        if (run is null) return;
        var k = offset - run.Start;
        var second = (Run)run.Element.CloneNode(false);
        if (run.Element.RunProperties is { } rPr) second.Append(rPr.CloneNode(true));

        var seen = 0;
        foreach (var child in run.Element.ChildElements.Where(c => c is not RunProperties).ToList())
        {
            var len = WordParagraphs.ChildText(child).Length;
            if (seen >= k)
            {
                child.Remove();
                second.Append(child);
            }
            else if (seen + len > k && child is Text t)
            {
                // The split falls inside this text node.
                var cut = k - seen;
                second.Append(new Text(t.Text[cut..]) { Space = SpaceProcessingModeValues.Preserve });
                t.Text = t.Text[..cut];
                t.Space = SpaceProcessingModeValues.Preserve;
            }
            seen += len;
        }
        run.Element.InsertAfterSelf(second);
    }

    static void RemoveText(Run run)
    {
        foreach (var child in run.ChildElements.Where(c => WordParagraphs.ChildText(c).Length > 0).ToList()) child.Remove();
    }

    /// <summary>Text with tabs and line breaks as Word elements.</summary>
    static void AppendText(Run run, string value)
    {
        var lines = value.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) run.Append(new Break());
            var pieces = lines[i].Split('\t');
            for (var j = 0; j < pieces.Length; j++)
            {
                if (j > 0) run.Append(new TabChar());
                if (pieces[j].Length > 0) run.Append(new Text(pieces[j]) { Space = SpaceProcessingModeValues.Preserve });
            }
        }
    }

    static void Highlight(Run run)
    {
        run.RunProperties ??= new RunProperties();
        run.RunProperties.Highlight = new Highlight { Val = HighlightColorValues.Yellow };
    }
}
