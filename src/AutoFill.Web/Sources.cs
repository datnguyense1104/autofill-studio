using System.Text.Json;
using AutoFill.Core;
using AutoFill.Data;
using AutoFill.Documents.Ingest;

namespace AutoFill.Web;

public sealed record SourceBody(string? Key, string? Name);

/// <summary>Reads one source file in the background (scans can take minutes); the document lands in the cache.</summary>
public sealed class SourceReadJob(FileService files, SourceService sources, Settings settings) : IJobHandler
{
    public const string JobType = "source.read";
    public sealed record Payload(Guid FileId);

    public string Type => JobType;

    public async Task<string?> RunAsync(Job job, IJobProgress progress, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<Payload>(job.Payload, SourceService.Json) ?? throw new InvalidOperationException("Empty payload");
        var file = await files.GetAsync(payload.FileId, ct);
        await progress.ReportAsync("Đọc file");
        var result = await sources.ReadAsync(file, (step, done, total) => progress.ReportAsync(step, done, total), ct);
        return JsonSerializer.Serialize(SourceEndpoints.Summary(file, result.Document, settings.MaxSourceChars, result.Cached), SourceService.Json);
    }
}

public static class SourceEndpoints
{
    /// <summary>What the user sees about a read file: size, how it was read, and whether it is too long.</summary>
    public static object Summary(StoredFile file, SourceDocument doc, int maxChars, bool cached)
    {
        var notes = doc.Notes.ToList();
        if (doc.CharCount > maxChars)
            notes.Add($"Văn bản dài {doc.CharCount:N0} ký tự, chỉ {maxChars:N0} ký tự đầu được dùng khi điền mẫu");
        return new
        {
            fileId = file.Id, name = file.Name, kind = doc.Kind, pages = doc.Pages.Count,
            aiPages = doc.Pages.Count(p => p.ReadByAi), chars = doc.CharCount, notes, cached,
        };
    }

    public static void MapSources(this IEndpointRouteBuilder app)
    {
        var sources = app.MapGroup("/api/sources").RequireAuthorization();

        // A file just uploaded becomes a workspace file and is read in the background.
        sources.MapPost("/", async (SourceBody body, HttpContext ctx, FileService files, JobQueue queue, CancellationToken ct) =>
        {
            var file = await files.TakeUploadAsync(body.Key ?? "", body.Name ?? "", ct);
            var job = await queue.EnqueueAsync(SourceReadJob.JobType, new SourceReadJob.Payload(file.Id), CurrentUser.From(ctx).Id, ct);
            return Results.Ok(new { fileId = file.Id, jobId = job.Id });
        });

        sources.MapGet("/{fileId:guid}", async (Guid fileId, FileService files, SourceService service, Settings settings, CancellationToken ct) =>
        {
            var file = await files.GetAsync(fileId, ct);
            var doc = await service.CachedAsync(file.Sha256, ct) ?? throw new UserFacingException("File này chưa được đọc xong", 404);
            var (text, truncated) = SourceText.Render(doc, settings.MaxSourceChars);
            return Results.Json(new { summary = Summary(file, doc, settings.MaxSourceChars, cached: true), text, truncated, document = doc }, SourceService.Json);
        });
    }
}
