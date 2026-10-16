using System.Text.Json;
using System.Text.Json.Serialization;
using AutoFill.Core;
using AutoFill.Documents.Ingest;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoFill.Data;

/// <summary>Source files of the current workspace, read once and cached by content (sha256).</summary>
public sealed class SourceService(AppDbContext db, FileService files, SourceReader reader)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public sealed record Result(SourceDocument Document, bool Cached);

    /// <param name="onProgress">(step, done, total), only called when the file is actually read</param>
    public async Task<Result> ReadAsync(StoredFile file, Func<string, int, int, Task>? onProgress = null, CancellationToken ct = default)
    {
        if (await CachedAsync(file.Sha256, ct) is { } cached) return new Result(cached, true);

        var content = await files.ReadAsync(file, ct);
        var ext = file.Key[(file.Key.LastIndexOf('.') + 1)..];
        var doc = await reader.ReadAsync(content, ext, onProgress, ct);
        await SaveAsync(file.Sha256, doc, ct);
        return new Result(doc, false);
    }

    /// <returns>null when the file has not been read yet (or was read by an older reader)</returns>
    public async Task<SourceDocument?> CachedAsync(string sha256, CancellationToken ct = default)
    {
        var row = await db.Transcripts.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Sha256 == sha256 && t.ReaderVersion == SourceReader.Version, ct);
        return row is null ? null : JsonSerializer.Deserialize<SourceDocument>(row.Document, Json);
    }

    async Task SaveAsync(string sha256, SourceDocument doc, CancellationToken ct)
    {
        var row = await db.Transcripts.FirstOrDefaultAsync(t => t.Sha256 == sha256, ct);
        if (row is null) db.Transcripts.Add(row = new Transcript { Sha256 = sha256, Document = "" });
        row.ReaderVersion = SourceReader.Version;
        row.Document = JsonSerializer.Serialize(doc, Json);
        row.Pages = doc.Pages.Count;
        row.AiPages = doc.Pages.Count(p => p.ReadByAi);
        row.CreatedAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException err) when (err.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another job read the same file at the same moment and saved first; its copy is as good.
            db.Entry(row).State = EntityState.Detached;
        }
    }
}
