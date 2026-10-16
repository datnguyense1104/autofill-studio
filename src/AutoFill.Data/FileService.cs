using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AutoFill.Core;
using Microsoft.EntityFrameworkCore;

namespace AutoFill.Data;

/// <summary>
/// Key layout (ASCII only: Supabase Storage rejects Vietnamese letters; the real name is kept in the database):
///   ws/{workspace}/{sha256}.{ext}     a workspace file, named by content, never overwritten
///   uploads/{workspace}/{uuid}.{ext}  a file the browser just uploaded, moved into ws/ when used
/// </summary>
public static partial class FileKeys
{
    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>
    {
        ["pdf"] = "application/pdf",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["doc"] = "application/msword",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["xls"] = "application/vnd.ms-excel",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
    };

    [GeneratedRegex(@"^(ws/[0-9a-f]{32}/[0-9a-f]{64}|uploads/[0-9a-f]{32}/[0-9a-f]{32})\.(pdf|docx|doc|xlsx|xls|png|jpg|jpeg)$")]
    private static partial Regex KeyPattern();

    public static bool IsValid(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <returns>the lower-case extension, or null when the file type is not accepted</returns>
    public static string? ExtensionOf(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ContentTypes.ContainsKey(ext) ? ext : null;
    }

    public static string Workspace(Guid workspaceId, string sha256, string ext) => $"ws/{workspaceId:N}/{sha256}.{ext}";
    public static string Upload(Guid workspaceId, string ext) => $"uploads/{workspaceId:N}/{Guid.NewGuid():N}.{ext}";
    public static string UploadPrefix(Guid workspaceId) => $"uploads/{workspaceId:N}/";
}

public sealed record FileLimits(long MaxBytes);

/// <summary>Files of the current workspace: stored once per content, read by id.</summary>
public sealed class FileService(AppDbContext db, IFileStore store, IWorkspaceScope scope, FileLimits limits)
{
    Guid WorkspaceId => scope.WorkspaceId ?? throw new UserFacingException("Chưa chọn không gian làm việc", 403);

    static string ExtensionOrThrow(string name) =>
        FileKeys.ExtensionOf(name) ?? throw new UserFacingException($"Loại file không được hỗ trợ: {name}");

    /// <summary>Saves bytes as a workspace file; the same content saved twice gives the same row.</summary>
    public async Task<StoredFile> SaveAsync(byte[] content, string name, CancellationToken ct = default)
    {
        if (content.LongLength > limits.MaxBytes) throw new UserFacingException($"File quá lớn (tối đa {limits.MaxBytes / 1024 / 1024} MB)");
        var ext = ExtensionOrThrow(name);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var existing = await db.Files.FirstOrDefaultAsync(f => f.Sha256 == sha && f.Key.EndsWith("." + ext), ct);
        if (existing is not null) return existing;

        var key = FileKeys.Workspace(WorkspaceId, sha, ext);
        await store.PutAsync(key, content, FileKeys.ContentTypes[ext], ct);
        var file = new StoredFile
        {
            Key = key, Name = name.Trim(), ContentType = FileKeys.ContentTypes[ext], Size = content.LongLength, Sha256 = sha,
        };
        db.Files.Add(file);
        await db.SaveChangesAsync(ct);
        return file;
    }

    public async Task<StoredFile> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Files.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new UserFacingException("Không tìm thấy file", 404);

    public async Task<byte[]> ReadAsync(StoredFile file, CancellationToken ct = default) =>
        await store.GetAsync(file.Key, ct) ?? throw new InvalidOperationException($"File missing from storage: {file.Key}");

    /// <summary>Where the browser uploads a file straight to storage; null url = post it to this server instead.</summary>
    public async Task<(string Key, string? Url)> CreateUploadAsync(string name, CancellationToken ct = default)
    {
        var key = FileKeys.Upload(WorkspaceId, ExtensionOrThrow(name));
        return (key, await store.CreateUploadUrlAsync(key, ct));
    }

    /// <summary>A file posted to this server (when the store has no direct upload).</summary>
    public async Task<string> ReceiveUploadAsync(byte[] content, string name, CancellationToken ct = default)
    {
        if (content.LongLength > limits.MaxBytes) throw new UserFacingException($"File quá lớn (tối đa {limits.MaxBytes / 1024 / 1024} MB)");
        var ext = ExtensionOrThrow(name);
        var key = FileKeys.Upload(WorkspaceId, ext);
        await store.PutAsync(key, content, FileKeys.ContentTypes[ext], ct);
        return key;
    }

    /// <summary>Turns an upload of this workspace into a workspace file, then deletes the upload.</summary>
    public async Task<StoredFile> TakeUploadAsync(string key, string name, CancellationToken ct = default)
    {
        if (!FileKeys.IsValid(key) || !key.StartsWith(FileKeys.UploadPrefix(WorkspaceId), StringComparison.Ordinal))
            throw new UserFacingException("File tải lên không hợp lệ");
        var content = await store.GetAsync(key, ct) ?? throw new UserFacingException("Không tìm thấy file vừa tải lên, hãy chọn lại file");
        var keyExt = key[(key.LastIndexOf('.') + 1)..];
        if (FileKeys.ExtensionOf(name) != keyExt) name = $"{Path.GetFileNameWithoutExtension(name)}.{keyExt}";
        var file = await SaveAsync(content, name, ct);
        await store.DeleteAsync(key, ct);
        return file;
    }
}
