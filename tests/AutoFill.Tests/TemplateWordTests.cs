using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Documents.Templates;
using AutoFill.Documents.Templates.Word;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Run = DocumentFormat.OpenXml.Wordprocessing.Run;

namespace AutoFill.Tests;

/// <summary>Answers every AI call with a fixed JSON built by the test, and keeps the requests.</summary>
public sealed class FakeAi(Func<AiRequest, JsonNode> answer) : IAiClient
{
    public List<AiRequest> Requests { get; } = [];

    public Task<JsonNode> GenerateJsonAsync(AiRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(answer(request));
    }
}

public static class WordFixtures
{
    public static Run R(string text, bool bold = false, bool italic = false)
    {
        var run = new Run();
        if (bold || italic)
        {
            var rPr = new RunProperties();
            if (bold) rPr.Bold = new Bold();
            if (italic) rPr.Italic = new Italic();
            run.Append(rPr);
        }
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    public static Paragraph P(params OpenXmlElement[] content) => new(content);
    public static Paragraph P(string text) => new(R(text));

    /// <summary>A paragraph with distinctive settings, to check that copies keep them.</summary>
    public static Paragraph Indented(string text, int left) =>
        new(new ParagraphProperties(new Indentation { Left = left.ToString() }), R(text));

    public static byte[] Docx(params OpenXmlElement[] body)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            doc.AddMainDocumentPart().Document = new Document(new Body(body));
        return ms.ToArray();
    }

    /// <summary>Paragraph texts of a .docx as the writer's own walk reads them.</summary>
    public static List<WordParagraph> Read(byte[] docx, out WordprocessingDocument doc)
    {
        doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return [.. WordParagraphs.Collect(doc)];
    }

    public static List<string> Texts(byte[] docx)
    {
        var paragraphs = Read(docx, out var doc);
        using (doc) return paragraphs.Select(p => p.Text).ToList();
    }
}

public sealed class TemplateWordTests
{
    static Slot Phrase(string id, int paragraph, int start, int length, bool whole = false) =>
        new() { Id = id, Label = id, Locations = [new PhraseLocation(paragraph, start, length, whole)] };

    static Dictionary<string, SlotValue> Values(params (string Id, string Text)[] values) =>
        values.ToDictionary(v => v.Id, v => new SlotValue(v.Text));

    [Fact]
    public void A_phrase_across_differently_formatted_runs_keeps_the_formatting_around_it()
    {
        // "Ông " plain, "Nguyễn " bold, "Văn A" italic, ", quốc tịch Úc." plain
        var template = WordFixtures.Docx(WordFixtures.P(
            WordFixtures.R("Ông "), WordFixtures.R("Nguyễn ", bold: true), WordFixtures.R("Văn A", italic: true), WordFixtures.R(", quốc tịch Úc.")));

        var filled = WordWriter.Write(template, [Phrase("s1", 0, 4, 12), Phrase("s2", 0, 28, 2)], Values(("s1", "Trần Thị B"), ("s2", "Anh")));

        var paragraphs = WordFixtures.Read(filled, out var doc);
        using (doc)
        {
            var p = paragraphs.Single();
            Assert.Equal("Ông Trần Thị B, quốc tịch Anh.", p.Text);
            var name = p.Runs.Single(r => r.Text == "Trần Thị B").Element;
            Assert.NotNull(name.RunProperties?.Bold);   // the phrase takes the look of its first run
            Assert.Null(p.Runs[0].Element.RunProperties?.Bold);
            Assert.Null(p.Runs.Single(r => r.Text.StartsWith(", quốc")).Element.RunProperties?.Italic);
        }
    }

    [Fact]
    public void List_entries_grow_or_disappear_with_the_number_of_people()
    {
        var template = WordFixtures.Docx(
            WordFixtures.P("Danh sách:"),
            WordFixtures.Indented("1. Nguyễn Văn A, hộ chiếu C1", 720),
            WordFixtures.Indented("2. Lê Văn C, hộ chiếu C2", 720),
            WordFixtures.P("Trân trọng."));
        Slot[] slots = [Phrase("a", 1, 0, 28, whole: true), Phrase("b", 2, 0, 24, whole: true)];

        var more = WordWriter.Write(template, slots, Values(("a", "1. Trần B, hộ chiếu X1\n2. Phạm D, hộ chiếu X2\n3. Võ E, hộ chiếu X3"), ("b", "")));
        Assert.Equal(["Danh sách:", "1. Trần B, hộ chiếu X1", "2. Phạm D, hộ chiếu X2", "3. Võ E, hộ chiếu X3", "Trân trọng."], WordFixtures.Texts(more));

        var copies = WordFixtures.Read(more, out var doc);
        using (doc) Assert.Equal("720", copies[3].Element.ParagraphProperties!.Indentation!.Left!.Value);
    }

    [Fact]
    public void A_section_is_rewritten_with_the_look_of_the_old_paragraphs_of_the_same_kind()
    {
        var template = WordFixtures.Docx(
            WordFixtures.P("Kính gửi: Tổng Lãnh sự quán Úc"),
            WordFixtures.Indented("Sở Ngoại vụ trân trọng thông báo vụ cũ.", 500),
            WordFixtures.Indented("- Ý kiến cũ thứ nhất", 900),
            WordFixtures.P("Nơi nhận:"));
        Slot section = new() { Id = "body", Label = "Phần thân", Locations = [new SectionLocation(1, 2)] };

        var filled = WordWriter.Write(template, [section], Values(("body", "Đoạn mới một.\n- Ý mới A\n- Ý mới B\nĐoạn kết.")));

        var paragraphs = WordFixtures.Read(filled, out var doc);
        using (doc)
        {
            Assert.Equal(["Kính gửi: Tổng Lãnh sự quán Úc", "Đoạn mới một.", "- Ý mới A", "- Ý mới B", "Đoạn kết.", "Nơi nhận:"], paragraphs.Select(p => p.Text));
            Assert.Equal(["500", "900", "900", "500"], paragraphs.Skip(1).Take(4).Select(p => p.Element.ParagraphProperties!.Indentation!.Left!.Value!));
        }
    }

    [Fact]
    public void Text_inside_a_word_field_is_not_overwritten()
    {
        var template = WordFixtures.Docx(WordFixtures.P(
            WordFixtures.R("Ngày "),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" DATE ")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            WordFixtures.R("15/10/2026"),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End })));

        Assert.Equal("Ngày 15/10/2026", WordFixtures.Texts(template).Single());
        Assert.Throws<UserFacingException>(() => WordWriter.Write(template, [Phrase("d", 0, 5, 10)], Values(("d", "16/10/2026"))));
    }

    [Fact]
    public void The_placer_finds_the_words_repeats_and_list_lines_and_drops_overlaps()
    {
        var outline = WordOutline.Read(WordFixtures.Docx(
            WordFixtures.P("Kính gửi: Tổng Lãnh sự quán Úc"),
            WordFixtures.P("Bị cáo Tô Thị Ba, quốc tịch Úc, sinh ngày 01/02/1990."),
            WordFixtures.P("1. Tô Thị Ba, hộ chiếu PA123"),
            WordFixtures.P("Nội dung vụ việc cũ dòng một."),
            WordFixtures.P("Nội dung vụ việc cũ dòng hai."),
            WordFixtures.P("Đề nghị Tổng Lãnh sự quán Úc thông báo cho Tô Thị Ba.")));
        static WordPhrasePick Pick(int p, string text, string label = "x") => new(p, text, label, "text", "extract", "", [], "", 0.9);

        var answer = new WordAnswer("Khi có bản án", [
            Pick(1, "Tô Thị Ba", "Họ tên"),            // repeats in P2 (list line, longer pick wins) and P5
            Pick(4, "Tổng Lãnh sự quán Úc", "Cơ quan"), // wrong paragraph number (it is in P0 and P5)
            Pick(2, "Tô Thị Ba, hộ chiếu PA123", "Người"),
            Pick(1, "quốc tịch Úc"), Pick(1, "Úc"),      // the shorter overlapping pick is dropped
            Pick(3, "dòng một"),                         // inside the section: ignored
            Pick(1, "không có trong mẫu"),
        ], [new WordSectionPick(3, 4, "Nội dung", "", "", 0.8)]);

        var (slots, notes) = WordSlotPlacer.Place(outline, answer, [], []);

        var name = slots.Single(s => s.Label == "Họ tên");
        Assert.Equal([(1, 7, 9), (5, 43, 9)], name.Locations.Cast<PhraseLocation>().Select(l => (l.Paragraph, l.Start, l.Length)));
        Assert.Equal([0, 5], slots.Single(s => s.Label == "Cơ quan").Locations.Cast<PhraseLocation>().Select(l => l.Paragraph));
        Assert.True(((PhraseLocation)slots.Single(s => s.Label == "Người").Locations.Single()).WholeParagraph);
        Assert.Equal(new SectionLocation(3, 4), slots.Single(s => s.Label == "Nội dung").Locations.Single());
        Assert.DoesNotContain(slots, s => s.SampleValue == "Úc");
        Assert.Contains(notes, n => n.Contains("không có trong mẫu"));
        Assert.Equal(slots.Select((_, i) => $"s{i + 1}"), slots.Select(s => s.Id));
    }

    [Fact]
    public async Task Differences_between_filled_copies_become_slots_even_when_the_ai_misses_them()
    {
        static byte[] Letter(string name, string date, params string[] body) => WordFixtures.Docx([
            WordFixtures.P("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM"),
            WordFixtures.P($"Kính gửi ông {name}, công dân Úc."),
            .. body.Select(WordFixtures.P),
            WordFixtures.P($"Ngày hẹn: {date}."),
        ]);
        var template = Letter("Nguyễn Văn A", "01/02/2026", "Thân một.", "Thân hai.");
        var other = Letter("Trần B", "03/04/2026", "Thân khác hẳn.");
        var ai = new FakeAi(_ => new JsonObject { ["useWhen"] = "Thư mời", ["phrases"] = new JsonArray(), ["sections"] = new JsonArray() });

        var analysis = await new TemplateAnalyzer(ai).AnalyzeAsync(new TemplateInput(template, TemplateKind.Word, "thu-moi.docx", [other], []));

        Assert.Equal(["Nguyễn Văn A", "Thân một.\nThân hai.", "01/02/2026"], analysis.Slots.Select(s => s.SampleValue));
        Assert.All(analysis.Slots, s => Assert.Equal(0.4, s.Confidence));
        var prompt = ai.Requests.Single().Input;
        Assert.Contains("P1: Kính gửi ông Nguyễn Văn A, công dân Úc.", prompt);
        Assert.Contains("P1: \"Nguyễn Văn A\"", prompt);
        Assert.Equal("Thư mời", analysis.UseWhen);
    }

    [Fact]
    public void Analysis_and_writing_agree_on_addresses_end_to_end()
    {
        var template = WordFixtures.Docx(
            WordFixtures.P(WordFixtures.R("Số hộ chiếu:\t"), WordFixtures.R("C1234567", bold: true)),
            WordFixtures.P("Họ tên: NGUYỄN VĂN A"));
        var outline = WordOutline.Read(template);
        // The AI sees the tab as a space and copies it that way.
        var answer = new WordAnswer("", [new WordPhrasePick(0, "chiếu: C1234567", "x", "text", "extract", "", [], "", 1),
            new WordPhrasePick(1, "NGUYỄN VĂN A", "Họ tên", "text", "extract", "", [], "", 1)], []);
        var (slots, _) = WordSlotPlacer.Place(outline, answer, [], []);

        var filled = WordWriter.Write(template, slots, Values(("s1", "chiếu:\tP7654321"), ("s2", "TRẦN THỊ B")), highlight: true);

        Assert.Equal(["Số hộ chiếu:\tP7654321", "Họ tên: TRẦN THỊ B"], WordFixtures.Texts(filled));
    }
}
