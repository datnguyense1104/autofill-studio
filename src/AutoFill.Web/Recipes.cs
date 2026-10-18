using System.Text.Json;
using AutoFill.Core;
using AutoFill.Data;
using AutoFill.Documents.Runs;
using AutoFill.Documents.Templates;
using Microsoft.EntityFrameworkCore;

namespace AutoFill.Web;

public sealed record RecipeBody(string? Name, Guid TemplateFileId, Slot[]? Slots, RecipeRule[]? Rules, string? Notes, string? UseWhen, bool? CheckMissing);
public sealed record SuggestBody(Guid[]? SourceFileIds);

public static class RecipeEndpoints
{
    static readonly HashSet<string> RuleTypes = ["dateAfter", "sameSentenceCount", "regex"];

    public static List<Slot> SlotsOf(RecipeVersion v) => JsonSerializer.Deserialize<List<Slot>>(v.Slots, SourceService.Json) ?? [];
    public static List<RecipeRule> RulesOf(RecipeVersion v) => JsonSerializer.Deserialize<List<RecipeRule>>(v.Rules, SourceService.Json) ?? [];

    static object VersionView(RecipeVersion v, StoredFile template) => new
    {
        id = v.Id, number = v.Number, templateFileId = v.TemplateFileId, templateName = template.Name, kind = v.Kind,
        slots = SlotsOf(v), rules = RulesOf(v), notes = v.Notes, checkMissing = v.CheckMissing, createdAt = v.CreatedAt,
    };

    /// <summary>A version that can run: a Word/Excel template, unique slot ids, locations of the template's kind, rules on real slots.</summary>
    static async Task<RecipeVersion> NewVersionAsync(RecipeBody body, FileService files, Guid userId, CancellationToken ct)
    {
        var template = await files.GetAsync(body.TemplateFileId, ct);
        var ext = template.Key[(template.Key.LastIndexOf('.') + 1)..];
        var kind = ext switch
        {
            "docx" => TemplateKind.Word,
            "xlsx" => TemplateKind.Excel,
            _ => throw new UserFacingException("File mẫu của công thức phải là .docx hoặc .xlsx"),
        };
        var slots = body.Slots ?? [];
        if (slots.Length == 0) throw new UserFacingException("Công thức cần ít nhất một ô cần điền");
        if (slots.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 60)) throw new UserFacingException("Mỗi ô cần có mã (tối đa 60 ký tự)");
        if (slots.Select(s => s.Id).Distinct().Count() != slots.Length) throw new UserFacingException("Mã ô bị trùng");
        if (slots.Any(s => s.Locations.Count == 0)) throw new UserFacingException("Mỗi ô cần ít nhất một vị trí trong mẫu");
        var wrongPlace = slots.FirstOrDefault(s => s.Locations.Any(l => kind == TemplateKind.Word ? l is CellLocation or RowsLocation : l is PhraseLocation or SectionLocation));
        if (wrongPlace is not null) throw new UserFacingException($"Ô \"{wrongPlace.Label}\" có vị trí không hợp với mẫu {(kind == TemplateKind.Word ? "Word" : "Excel")}");
        var ids = slots.Select(s => s.Id).ToHashSet();
        foreach (var rule in body.Rules ?? [])
        {
            if (!RuleTypes.Contains(rule.Type)) throw new UserFacingException($"Loại kiểm tra không hỗ trợ: {rule.Type}");
            if (!ids.Contains(rule.Slot) || (rule.Other is not null && !ids.Contains(rule.Other))) throw new UserFacingException("Kiểm tra trỏ tới ô không có trong công thức");
        }
        return new RecipeVersion
        {
            TemplateFileId = template.Id, Kind = kind.ToString().ToLowerInvariant(), CreatedBy = userId,
            Slots = JsonSerializer.Serialize(slots, SourceService.Json), Rules = JsonSerializer.Serialize(body.Rules ?? [], SourceService.Json),
            Notes = body.Notes?.Trim() ?? "", CheckMissing = body.CheckMissing ?? kind == TemplateKind.Word,
        };
    }

    /// <summary>A new recipe at version 1 (from the builder, or straight from a template analysis).</summary>
    public static async Task<Guid> CreateAsync(RecipeBody body, AppDbContext db, FileService files, Guid userId, CancellationToken ct)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200) throw new UserFacingException("Hãy đặt tên cho công thức (tối đa 200 ký tự)");
        var version = await NewVersionAsync(body, files, userId, ct);
        var recipe = new Recipe { Name = name, UseWhen = body.UseWhen?.Trim() ?? "", CurrentVersion = 1 };
        version.RecipeId = recipe.Id;
        version.Number = 1;
        db.Recipes.Add(recipe);
        db.RecipeVersions.Add(version);
        await db.SaveChangesAsync(ct);
        return recipe.Id;
    }

    public static void MapRecipes(this IEndpointRouteBuilder app)
    {
        var recipes = app.MapGroup("/api/recipes").RequireAuthorization();

        recipes.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Recipes.AsNoTracking().Where(r => r.DeletedAt == null).OrderByDescending(r => r.UpdatedAt)
                .Select(r => new { r.Id, r.Name, r.UseWhen, r.CurrentVersion, r.UpdatedAt }).ToListAsync(ct)));

        recipes.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var recipe = await db.Recipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct) ?? throw new UserFacingException("Không tìm thấy công thức", 404);
            var version = await db.RecipeVersions.AsNoTracking().FirstAsync(v => v.RecipeId == id && v.Number == recipe.CurrentVersion, ct);
            var template = await db.Files.AsNoTracking().FirstAsync(f => f.Id == version.TemplateFileId, ct);
            return Results.Json(new { recipe.Id, recipe.Name, recipe.UseWhen, version = VersionView(version, template) }, SourceService.Json);
        });

        recipes.MapPost("/", async (RecipeBody body, HttpContext ctx, AppDbContext db, FileService files, CancellationToken ct) =>
        {
            var me = CurrentUser.From(ctx);
            me.RequireEditor();
            return Results.Ok(new { id = await CreateAsync(body, db, files, me.Id, ct), version = 1 });
        });

        // Saving a change makes a new version; runs keep the version they used.
        recipes.MapPost("/{id:guid}/versions", async (Guid id, RecipeBody body, HttpContext ctx, AppDbContext db, FileService files, CancellationToken ct) =>
        {
            var recipe = await db.Recipes.FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct) ?? throw new UserFacingException("Không tìm thấy công thức", 404);
            var me = CurrentUser.From(ctx);
            me.RequireEditor();
            var version = await NewVersionAsync(body, files, me.Id, ct);
            version.RecipeId = id;
            version.Number = recipe.CurrentVersion + 1;
            recipe.CurrentVersion = version.Number;
            if (body.Name?.Trim() is { Length: > 0 and <= 200 } name) recipe.Name = name;
            if (body.UseWhen is not null) recipe.UseWhen = body.UseWhen.Trim();
            recipe.UpdatedAt = DateTimeOffset.UtcNow;
            db.RecipeVersions.Add(version);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id, version = version.Number });
        });

        // Deleted recipes leave the list and new runs; past runs keep their version and still open.
        recipes.MapDelete("/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            CurrentUser.From(ctx).RequireEditor();
            var recipe = await db.Recipes.FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct) ?? throw new UserFacingException("Không tìm thấy công thức", 404);
            recipe.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        recipes.MapPost("/suggest", async (SuggestBody body, HttpContext ctx, FileService files, JobQueue queue, CancellationToken ct) =>
        {
            var ids = body.SourceFileIds ?? [];
            if (ids.Length == 0) throw new UserFacingException("Hãy chọn tài liệu nguồn");
            foreach (var id in ids) await files.GetAsync(id, ct);
            var job = await queue.EnqueueAsync(RecipeSuggestJob.JobType, new RecipeSuggestJob.Payload(ids), CurrentUser.From(ctx).Id, ct);
            return Results.Ok(new { jobId = job.Id });
        });
    }
}

/// <summary>Reads the new sources (scans may take a while) and asks which recipes fit them.</summary>
public sealed class RecipeSuggestJob(AppDbContext db, RunService runs, RecipeSuggester suggester) : IJobHandler
{
    public const string JobType = "recipe.suggest";
    public sealed record Payload(Guid[] SourceFileIds);

    public string Type => JobType;

    public async Task<string?> RunAsync(Job job, IJobProgress progress, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<Payload>(job.Payload, SourceService.Json) ?? throw new InvalidOperationException("Empty payload");
        var read = await runs.ReadSourcesAsync(payload.SourceFileIds, progress, ct);
        var cards = await db.Recipes.AsNoTracking().Where(r => r.DeletedAt == null).Select(r => new RecipeCard(r.Id, r.Name, r.UseWhen)).ToListAsync(ct);
        await progress.ReportAsync("AI chọn công thức");
        return JsonSerializer.Serialize(new { suggestions = await suggester.SuggestAsync(read, cards, ct) }, SourceService.Json);
    }
}
