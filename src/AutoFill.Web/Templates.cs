using System.Text.Json;
using AutoFill.Core;
using AutoFill.Data;
using AutoFill.Documents.Ingest;
using AutoFill.Documents.Templates;
using Microsoft.EntityFrameworkCore;

namespace AutoFill.Web;

/// <param name="Key">an upload to take; or <paramref name="FileId"/>, a template already uploaded (to analyse again)</param>
/// <param name="SkipAi">only prepare the template (convert .doc/.xls); the user marks the slots by hand</param>
/// <param name="CreateRecipe">save the analysis as a new recipe straight away (named after the file): upload and done</param>
/// <param name="AllowDuplicate">create the recipe even when an existing one has the same form (the user said so)</param>
public sealed record TemplateBody(string? Key, string? Name, Guid[]? FilledFileIds, Guid[]? ExampleFileIds, string? Notes,
    Guid? FileId = null, bool SkipAi = false, bool CreateRecipe = false, bool AllowDuplicate = false);
public sealed record FillBody(Slot[]? Slots, Dictionary<string, SlotValue>? Values, bool Highlight = true);

/// <summary>
/// Analyses a template in the background. An old .doc/.xls template is converted once and the
/// .docx/.xlsx copy becomes the template, so analysis and writing always use the same file.
/// </summary>
public sealed class TemplateAnalyzeJob(AppDbContext db, FileService files, SourceService sources, LegacyConverter legacy, TemplateAnalyzer analyzer) : IJobHandler
{
    public const string JobType = "template.analyze";
    public sealed record Payload(Guid FileId, Guid[] FilledFileIds, Guid[] ExampleFileIds, string? Notes, bool SkipAi = false, bool CreateRecipe = false, bool AllowDuplicate = false);

    public string Type => JobType;

    public async Task<string?> RunAsync(Job job, IJobProgress progress, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<Payload>(job.Payload, SourceService.Json) ?? throw new InvalidOperationException("Empty payload");
        await progress.ReportAsync("Chuẩn bị file mẫu");
        var template = await TemplateFileAsync(await files.GetAsync(payload.FileId, ct), ct);
        var kind = TemplateFiller.KindOf(Ext(template))!.Value;
        if (payload.SkipAi)
            return JsonSerializer.Serialize(new { templateFileId = template.Id, name = template.Name, analysis = new TemplateAnalysis(kind, []) }, SourceService.Json);

        // Checked before the AI (minutes): the same form uploaded again, often a filled copy of it.
        if (payload.CreateRecipe && !payload.AllowDuplicate && await SameFormAsync(template, kind, ct) is { } duplicate)
            return JsonSerializer.Serialize(new { templateFileId = template.Id, name = template.Name, duplicate }, SourceService.Json);

        var filled = new List<byte[]>();
        foreach (var id in payload.FilledFileIds)
        {
            var copy = await TemplateFileAsync(await files.GetAsync(id, ct), ct);
            if (TemplateFiller.KindOf(Ext(copy)) != kind) throw new UserFacingException($"Bản đã điền \"{copy.Name}\" khác loại với file mẫu");
            filled.Add(await files.ReadAsync(copy, ct));
        }
        var examples = new List<SourceDocument>();
        for (var i = 0; i < payload.ExampleFileIds.Length; i++)
        {
            await progress.ReportAsync("Đọc tài liệu nguồn mẫu", i, payload.ExampleFileIds.Length);
            examples.Add((await sources.ReadAsync(await files.GetAsync(payload.ExampleFileIds[i], ct), ct: ct)).Document);
        }

        await progress.ReportAsync("AI phân tích mẫu");
        var analysis = await analyzer.AnalyzeAsync(new TemplateInput(await files.ReadAsync(template, ct), kind, template.Name, filled, examples, payload.Notes), ct);

        Guid? recipeId = null;
        if (payload.CreateRecipe)
        {
            if (analysis.Slots.Count == 0)
                throw new UserFacingException($"AI không tìm thấy chỗ cần điền trong \"{template.Name}\". Hãy thử lại, hoặc mở trang tạo công thức chi tiết để tự chọn");
            await progress.ReportAsync("Lưu công thức");
            recipeId = await RecipeEndpoints.CreateAsync(new RecipeBody(
                Name: Path.GetFileNameWithoutExtension(template.Name), TemplateFileId: template.Id, Slots: [.. analysis.Slots],
                Rules: [], Notes: payload.Notes, UseWhen: analysis.UseWhen, CheckMissing: null), db, files, job.CreatedBy ?? Guid.Empty, ct);
        }
        return JsonSerializer.Serialize(new { templateFileId = template.Id, name = template.Name, analysis, recipeId }, SourceService.Json);
    }

    /// <summary>The recipe whose current template reads most like this one, when they are the same form.</summary>
    async Task<object?> SameFormAsync(StoredFile template, TemplateKind kind, CancellationToken ct)
    {
        var kindName = kind.ToString().ToLowerInvariant();
        var recipes = await db.Recipes.AsNoTracking().Where(r => r.DeletedAt == null)
            .Join(db.RecipeVersions.AsNoTracking(), r => new { Id = r.Id, N = r.CurrentVersion }, v => new { Id = v.RecipeId, N = v.Number },
                (r, v) => new { r.Id, r.Name, v.TemplateFileId, v.Kind })
            .Where(x => x.Kind == kindName).ToListAsync(ct);
        if (recipes.Count == 0) return null;
        var text = TemplateSimilarity.TextOf(await files.ReadAsync(template, ct), kind);
        (Guid Id, string Name, double Score)? best = null;
        foreach (var r in recipes)
        {
            var score = r.TemplateFileId == template.Id ? 1
                : TemplateSimilarity.Score(text, TemplateSimilarity.TextOf(await files.ReadAsync(await files.GetAsync(r.TemplateFileId, ct), ct), kind));
            if (score >= TemplateSimilarity.SameForm && (best is null || score > best.Value.Score)) best = (r.Id, r.Name, score);
        }
        return best is { } b ? new { recipeId = b.Id, name = b.Name, similarity = Math.Round(b.Score, 2) } : null;
    }

    static string Ext(StoredFile f) => f.Key[(f.Key.LastIndexOf('.') + 1)..];

    async Task<StoredFile> TemplateFileAsync(StoredFile file, CancellationToken ct)
    {
        var ext = Ext(file);
        if (TemplateFiller.KindOf(ext) is null) throw new UserFacingException($"\"{file.Name}\" không phải file Word/Excel, không dùng làm mẫu được");
        if (ext is not ("doc" or "xls")) return file;
        var converted = await legacy.ConvertAsync(await files.ReadAsync(file, ct), ext, ct);
        return await files.SaveAsync(converted, $"{Path.GetFileNameWithoutExtension(file.Name)}.{ext}x", ct);
    }
}

public static class TemplateEndpoints
{
    public static void MapTemplates(this IEndpointRouteBuilder app)
    {
        var templates = app.MapGroup("/api/templates").RequireAuthorization();

        // Upload a template (and optionally point at filled copies / source examples already uploaded) → analysis job.
        // What the builder and the review screen draw: paragraphs (Word) or cells (Excel), with the addresses slots use.
        templates.MapGet("/{fileId:guid}/outline", async (Guid fileId, FileService files, CancellationToken ct) =>
        {
            var file = await files.GetAsync(fileId, ct);
            var content = await files.ReadAsync(file, ct);
            return file.Key.EndsWith(".docx", StringComparison.Ordinal)
                ? Results.Json(new { kind = "word", paragraphs = Documents.Templates.Word.WordOutline.Read(content) }, SourceService.Json)
                : file.Key.EndsWith(".xlsx", StringComparison.Ordinal)
                    ? Results.Json(new { kind = "excel", sheets = Documents.Templates.Excel.ExcelOutline.Read(content) }, SourceService.Json)
                    : throw new UserFacingException("Chỉ xem trước được mẫu .docx/.xlsx");
        });

        templates.MapPost("/", async (TemplateBody body, HttpContext ctx, FileService files, JobQueue queue, CancellationToken ct) =>
        {
            CurrentUser.From(ctx).RequireEditor();
            var file = body.FileId is { } existing ? await files.GetAsync(existing, ct) : null;
            if (TemplateFiller.KindOf(FileKeys.ExtensionOf(file?.Name ?? body.Name ?? "") ?? "") is null)
                throw new UserFacingException("File mẫu phải là Word (.docx, .doc) hoặc Excel (.xlsx, .xls)");
            file ??= await files.TakeUploadAsync(body.Key ?? "", body.Name ?? "", ct);
            foreach (var id in (body.FilledFileIds ?? []).Concat(body.ExampleFileIds ?? [])) await files.GetAsync(id, ct);
            var job = await queue.EnqueueAsync(TemplateAnalyzeJob.JobType,
                new TemplateAnalyzeJob.Payload(file.Id, body.FilledFileIds ?? [], body.ExampleFileIds ?? [], body.Notes, body.SkipAi, body.CreateRecipe && !body.SkipAi, body.AllowDuplicate), CurrentUser.From(ctx).Id, ct);
            return Results.Ok(new { fileId = file.Id, jobId = job.Id });
        });

        // Writes values into a copy of the template and returns it (used by review/export, and to try a recipe).
        templates.MapPost("/{fileId:guid}/fill", async (Guid fileId, FillBody body, FileService files, CancellationToken ct) =>
        {
            var file = await files.GetAsync(fileId, ct);
            var ext = file.Key[(file.Key.LastIndexOf('.') + 1)..];
            if (ext is not ("docx" or "xlsx")) throw new UserFacingException("Chỉ ghi được vào mẫu .docx/.xlsx (mẫu .doc/.xls đã được chuyển khi phân tích)");
            var bytes = TemplateFiller.Fill(await files.ReadAsync(file, ct), TemplateFiller.KindOf(ext)!.Value,
                body.Slots ?? [], body.Values ?? [], body.Highlight);
            return Results.File(bytes, file.ContentType, file.Name);
        });
    }
}
