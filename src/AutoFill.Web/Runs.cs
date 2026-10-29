using System.Text.Json;
using System.Text.Json.Nodes;
using AutoFill.Core;
using AutoFill.Data;
using AutoFill.Documents.Runs;
using AutoFill.Documents.Templates;
using AutoFill.Documents.Templates.Excel;
using Microsoft.EntityFrameworkCore;

namespace AutoFill.Web;

public sealed record RunBody(Guid RecipeId, Guid[]? SourceFileIds);
public sealed record ReviewBody(SlotValue? Value, bool Reviewed = true);
public sealed record ReviseBody(string? Instruction);
public sealed record MissingBody(string? Decision);

/// <summary>What a run needs from the database and the file store: its version, sources and row labels.</summary>
public sealed class RunService(AppDbContext db, FileService files, SourceService sources)
{
    sealed class NoProgress : IJobProgress
    {
        public Task ReportAsync(string step, int? done = null, int? total = null) => Task.CompletedTask;
    }

    public static readonly IJobProgress Silent = new NoProgress();

    public async Task<(Run Run, RecipeVersion Version, Recipe Recipe)> LoadAsync(Guid runId, bool track, CancellationToken ct)
    {
        var runs = track ? db.Runs : db.Runs.AsNoTracking();
        var run = await runs.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new UserFacingException("Không tìm thấy lượt chạy", 404);
        var version = await db.RecipeVersions.AsNoTracking().FirstAsync(v => v.Id == run.RecipeVersionId, ct);
        var recipe = await db.Recipes.AsNoTracking().FirstAsync(r => r.Id == version.RecipeId, ct);
        return (run, version, recipe);
    }

    public static Guid[] SourceIds(Run run) => JsonSerializer.Deserialize<Guid[]>(run.SourceFileIds) ?? [];

    public async Task<List<RunSource>> ReadSourcesAsync(IReadOnlyList<Guid> ids, IJobProgress progress, CancellationToken ct)
    {
        var read = new List<RunSource>();
        for (var i = 0; i < ids.Count; i++)
        {
            var file = await files.GetAsync(ids[i], ct);
            await progress.ReportAsync($"Đọc nguồn: {file.Name}", i, ids.Count);
            var doc = (await sources.ReadAsync(file, (step, done, total) => progress.ReportAsync($"{file.Name}: {step}", done, total), ct)).Document;
            read.Add(new RunSource(file.Id, file.Name, doc));
        }
        return read;
    }

    public async Task<RunInput> InputAsync(Run run, RecipeVersion version, IJobProgress progress, CancellationToken ct)
    {
        var slots = RecipeEndpoints.SlotsOf(version);
        var read = await ReadSourcesAsync(SourceIds(run), progress, ct);
        return new RunInput(slots, read, await RowLabelsAsync(slots, version, ct), RecipeEndpoints.RulesOf(version), version.Notes, version.CheckMissing);
    }

    /// <summary>For row rules: each row's label (the question / field name), read from the template.</summary>
    async Task<Dictionary<string, IReadOnlyDictionary<int, string>>> RowLabelsAsync(List<Slot> slots, RecipeVersion version, CancellationToken ct)
    {
        var labels = new Dictionary<string, IReadOnlyDictionary<int, string>>();
        var rowSlots = slots.Where(s => s.Locations.Any(l => l is RowsLocation)).ToList();
        if (rowSlots.Count == 0) return labels;
        var sheets = ExcelOutline.Read(await files.ReadAsync(await files.GetAsync(version.TemplateFileId, ct), ct));
        foreach (var slot in rowSlots)
        {
            var at = slot.Locations.OfType<RowsLocation>().First();
            var sheet = sheets.FirstOrDefault(s => s.Name == at.Sheet);
            labels[slot.Id] = at.Rows.ToDictionary(r => r, r => sheet?.Find($"{at.LabelColumn}{r}")?.Text ?? "");
        }
        return labels;
    }

    public static RunValue ToRow(Guid runId, string slotId, SlotResult r) => new()
    {
        RunId = runId, SlotId = slotId, Value = JsonSerializer.Serialize(r.Value, SourceService.Json),
        Evidence = JsonSerializer.Serialize(r.Evidence, SourceService.Json), Warnings = JsonSerializer.Serialize(r.Warnings, SourceService.Json),
        Confidence = r.Confidence, Origin = r.Origin.ToString().ToLowerInvariant(), Note = r.Note,
    };

    /// <summary>The value that goes into the file: the reviewer's when there is one.</summary>
    public static SlotValue Final(RunValue v) =>
        JsonSerializer.Deserialize<SlotValue>(v.ReviewedValue ?? v.Value, SourceService.Json) ?? new SlotValue("");

    /// <summary>"Bỏ qua": the reviewer left an empty place empty on purpose (reviewed, no value of their own).</summary>
    public static bool IsSkipped(RunValue v) => v.Reviewed && v.ReviewedValue is null && IsEmpty(Final(v));

    public static bool IsEmpty(SlotValue v) => v.Rows is { } rows ? rows.Values.All(string.IsNullOrWhiteSpace) : string.IsNullOrWhiteSpace(v.Text);
}

/// <summary>
/// One run in the background: read the sources (cached), fill every slot with the recipe's
/// current version, save the values for review.
/// </summary>
public sealed class RunJob(AppDbContext db, RunService service, RunEngine engine) : IJobHandler
{
    public const string JobType = "run.execute";
    public sealed record Payload(Guid RunId);

    public string Type => JobType;

    public async Task<string?> RunAsync(Job job, IJobProgress progress, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<Payload>(job.Payload, SourceService.Json) ?? throw new InvalidOperationException("Empty payload");
        var (run, version, _) = await service.LoadAsync(payload.RunId, track: true, ct);
        run.Status = RunStatus.Running;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var input = await service.InputAsync(run, version, progress, ct);
        var result = await engine.RunAsync(input, (step, done, total) => progress.ReportAsync(step, done, total), ct);

        // A rerun replaces the earlier values.
        await db.RunValues.Where(v => v.RunId == run.Id).ExecuteDeleteAsync(ct);
        foreach (var slot in input.Slots) db.RunValues.Add(RunService.ToRow(run.Id, slot.Id, result.Values[slot.Id]));
        var usage = await db.AiLogs.Where(l => l.JobId == job.Id).GroupBy(_ => 1)
            .Select(g => new { Input = g.Sum(l => l.InputTokens), Output = g.Sum(l => l.OutputTokens) }).FirstOrDefaultAsync(ct);
        run.Missing = JsonSerializer.Serialize(result.Missing, SourceService.Json);
        run.Notes = JsonSerializer.Serialize(result.Notes, SourceService.Json);
        run.InputTokens = usage?.Input ?? 0;
        run.OutputTokens = usage?.Output ?? 0;
        run.Status = RunStatus.NeedsReview;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return JsonSerializer.Serialize(new
        {
            runId = run.Id, slots = input.Slots.Count, needsReview = result.Values.Values.Count(v => v.NeedsReview), missing = result.Missing.Count,
        }, SourceService.Json);
    }
}

public static class RunEndpoints
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static object ValueView(RunValue v) => new
    {
        v.SlotId, value = J(v.Value), reviewedValue = v.ReviewedValue is null ? (JsonElement?)null : J(v.ReviewedValue),
        evidence = J(v.Evidence), warnings = J(v.Warnings), v.Confidence, v.Origin, v.Note, v.Reviewed,
    };

    static async Task<RunValue> ValueAsync(AppDbContext db, Guid runId, string slotId, CancellationToken ct) =>
        await db.RunValues.FirstOrDefaultAsync(v => v.RunId == runId && v.SlotId == slotId, ct) ?? throw new UserFacingException("Không có ô này trong lượt chạy", 404);

    public static void MapRuns(this IEndpointRouteBuilder app)
    {
        var runs = app.MapGroup("/api/runs").RequireAuthorization();

        runs.MapPost("/", async (RunBody body, HttpContext ctx, AppDbContext db, FileService files, JobQueue queue, CancellationToken ct) =>
        {
            var recipe = await db.Recipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == body.RecipeId && r.DeletedAt == null, ct) ?? throw new UserFacingException("Không tìm thấy công thức", 404);
            var version = await db.RecipeVersions.AsNoTracking().FirstAsync(v => v.RecipeId == recipe.Id && v.Number == recipe.CurrentVersion, ct);
            var ids = body.SourceFileIds ?? [];
            if (ids.Length == 0) throw new UserFacingException("Hãy chọn tài liệu nguồn");
            foreach (var id in ids) await files.GetAsync(id, ct);

            var run = new Run { RecipeVersionId = version.Id, SourceFileIds = JsonSerializer.Serialize(ids), CreatedBy = CurrentUser.From(ctx).Id };
            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);
            var job = await queue.EnqueueAsync(RunJob.JobType, new RunJob.Payload(run.Id), run.CreatedBy, ct);
            run.JobId = job.Id;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { runId = run.Id, jobId = job.Id });
        });

        runs.MapGet("/", async (AppDbContext db, int? limit, CancellationToken ct) =>
            Results.Ok(await (from r in db.Runs.AsNoTracking()
                              join v in db.RecipeVersions on r.RecipeVersionId equals v.Id
                              join recipe in db.Recipes on v.RecipeId equals recipe.Id
                              orderby r.CreatedAt descending
                              select new { r.Id, status = r.Status.ToString(), recipeId = recipe.Id, recipe = recipe.Name, version = v.Number, r.JobId, r.CreatedAt, r.UpdatedAt })
                .Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct)));

        runs.MapGet("/{id:guid}", async (Guid id, AppDbContext db, RunService service, CancellationToken ct) =>
        {
            var (run, version, recipe) = await service.LoadAsync(id, track: false, ct);
            var job = run.JobId is { } jobId ? await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct) : null;
            var sourceIds = RunService.SourceIds(run);
            var names = await db.Files.AsNoTracking().Where(f => sourceIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
            var values = await db.RunValues.AsNoTracking().Where(v => v.RunId == id).ToListAsync(ct);

            // A run whose job gave up shows as failed, with the job's message.
            var failed = job?.Status == JobStatus.Failed && run.Status is RunStatus.Queued or RunStatus.Running;
            return Results.Json(new
            {
                run.Id, status = failed ? "failed" : run.Status.ToString(), error = failed ? job!.Error : null, jobId = run.JobId,
                recipe = new { recipe.Id, recipe.Name, version = version.Number, versionId = version.Id, templateFileId = version.TemplateFileId, kind = version.Kind },
                sources = sourceIds.Select(f => new { fileId = f, name = names.GetValueOrDefault(f, "") }),
                slots = RecipeEndpoints.SlotsOf(version),
                values = values.Select(ValueView),
                missing = J(run.Missing), notes = J(run.Notes),
                tokens = new { input = run.InputTokens, output = run.OutputTokens },
                run.CreatedAt, run.UpdatedAt,
            }, SourceService.Json);
        });

        // The reviewer's value for one slot (null value = back to what was filled).
        runs.MapPut("/{id:guid}/values/{slotId}", async (Guid id, string slotId, ReviewBody body, AppDbContext db, CancellationToken ct) =>
        {
            var row = await ValueAsync(db, id, slotId, ct);
            row.ReviewedValue = body.Value is null ? null : JsonSerializer.Serialize(body.Value, SourceService.Json);
            row.Reviewed = body.Reviewed;
            await db.SaveChangesAsync(ct);
            return Results.Json(ValueView(row), SourceService.Json);
        });

        // The AI writes one slot again, following the reviewer's instruction; checks run again.
        runs.MapPost("/{id:guid}/values/{slotId}/revise", async (Guid id, string slotId, ReviseBody body, AppDbContext db, RunService service, RunEngine engine, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Instruction)) throw new UserFacingException("Hãy ghi cần sửa thế nào");
            var (run, version, _) = await service.LoadAsync(id, track: false, ct);
            var row = await ValueAsync(db, id, slotId, ct);
            var input = await service.InputAsync(run, version, RunService.Silent, ct);
            var slot = input.Slots.First(s => s.Id == slotId);
            var result = await engine.ReviseAsync(slot, input, RunService.Final(row), body.Instruction.Trim(), ct);
            var fresh = RunService.ToRow(id, slotId, result);
            (row.Value, row.Evidence, row.Warnings, row.Confidence, row.Origin, row.Note) = (fresh.Value, fresh.Evidence, fresh.Warnings, fresh.Confidence, fresh.Origin, fresh.Note);
            row.ReviewedValue = null;
            row.Reviewed = false;
            await db.SaveChangesAsync(ct);
            return Results.Json(ValueView(row), SourceService.Json);
        });

        // "Có thể còn thiếu": the reviewer added it (usually through a revise) or decided it is not needed.
        runs.MapPost("/{id:guid}/missing/{index:int}", async (Guid id, int index, MissingBody body, RunService service, AppDbContext db, CancellationToken ct) =>
        {
            if (body.Decision is not ("added" or "ignored")) throw new UserFacingException("Lựa chọn không hợp lệ");
            var (run, _, _) = await service.LoadAsync(id, track: true, ct);
            var items = JsonNode.Parse(run.Missing)!.AsArray();
            if (index < 0 || index >= items.Count) throw new UserFacingException("Không có mục này", 404);
            items[index]!["decision"] = body.Decision;
            run.Missing = items.ToJsonString();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok = true });
        });

        // The filled file. Refused while a required slot has no value; warnings were for the reviewer to confirm.
        runs.MapGet("/{id:guid}/export", async (Guid id, string? fileName, bool? highlight, AppDbContext db, FileService files, RunService service, CancellationToken ct) =>
        {
            var (run, version, recipe) = await service.LoadAsync(id, track: true, ct);
            if (run.Status is RunStatus.Queued or RunStatus.Running) throw new UserFacingException("Lượt chạy chưa xong");
            var slots = RecipeEndpoints.SlotsOf(version);
            var rows = await db.RunValues.AsNoTracking().Where(v => v.RunId == id).ToDictionaryAsync(v => v.SlotId, ct);
            var values = slots.ToDictionary(s => s.Id, s => rows.TryGetValue(s.Id, out var v) ? RunService.Final(v) : new SlotValue(""));
            var blocking = slots.Where(s => s.Required && RunService.IsEmpty(values[s.Id]) && !(rows.TryGetValue(s.Id, out var v) && RunService.IsSkipped(v)))
                .Select(s => s.Label).ToList();
            if (blocking.Count > 0) throw new UserFacingException($"Còn ô bắt buộc chưa có giá trị: {string.Join(", ", blocking.Take(5))}{(blocking.Count > 5 ? "…" : "")}");

            var template = await files.GetAsync(version.TemplateFileId, ct);
            var kind = version.Kind == "excel" ? TemplateKind.Excel : TemplateKind.Word;
            var bytes = TemplateFiller.Fill(await files.ReadAsync(template, ct), kind, slots, values, highlight: highlight ?? false);
            run.Status = RunStatus.Done;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            var ext = kind == TemplateKind.Excel ? "xlsx" : "docx";
            var wanted = string.IsNullOrWhiteSpace(fileName) ? $"{recipe.Name} - {DateTime.Now:yyyy-MM-dd}" : fileName.Trim();
            if (wanted.EndsWith($".{ext}", StringComparison.OrdinalIgnoreCase)) wanted = wanted[..^(ext.Length + 1)];
            var name = string.Concat(wanted.Take(150).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Results.File(bytes, template.ContentType, $"{name}.{ext}");
        });
    }
}
