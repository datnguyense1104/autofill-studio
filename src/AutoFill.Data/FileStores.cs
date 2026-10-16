using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AutoFill.Core;

namespace AutoFill.Data;

/// <summary>Files in a folder on this machine (development, tests).</summary>
public sealed class DiskFileStore(string root) : IFileStore
{
    string PathOf(string key) => Path.Combine([root, .. key.Split('/')]);

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default)
    {
        var file = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, content, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var file = PathOf(key);
        return File.Exists(file) ? await File.ReadAllBytesAsync(file, ct) : null;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }

    public Task<string?> CreateUploadUrlAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

public sealed record SupabaseStorageOptions(string Url, string ServiceKey, string Bucket, long MaxBytes);

/// <summary>
/// A PRIVATE Supabase Storage bucket, read and written only by this server (service key).
/// Browsers get one-off signed upload URLs, so large scans never pass through the server.
/// </summary>
public sealed class SupabaseFileStore(HttpClient http, SupabaseStorageOptions options) : IFileStore
{
    readonly SemaphoreSlim bucketLock = new(1, 1);
    bool bucketReady;

    HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{options.Url.TrimEnd('/')}/storage/v1/{path}");
        req.Headers.Add("apikey", options.ServiceKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceKey);
        return req;
    }

    string ObjectPath(string key) => $"{options.Bucket}/{key}";

    /// <summary>Creates the private bucket on first use; an existing one gets the current settings.</summary>
    async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (bucketReady) return;
        await bucketLock.WaitAsync(ct);
        try
        {
            if (bucketReady) return;
            var settings = new JsonObject
            {
                ["public"] = false,
                ["file_size_limit"] = options.MaxBytes,
                ["allowed_mime_types"] = new JsonArray([.. FileKeys.ContentTypes.Values.Distinct().Select(v => JsonValue.Create(v))]),
            };
            var create = Request(HttpMethod.Post, "bucket");
            var body = settings.DeepClone().AsObject();
            body["id"] = options.Bucket;
            body["name"] = options.Bucket;
            create.Content = JsonContent.Create(body);
            using var res = await http.SendAsync(create, ct);
            if (!res.IsSuccessStatusCode)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                if (!text.Contains("already exists", StringComparison.OrdinalIgnoreCase) && !text.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Supabase Storage: cannot create bucket ({(int)res.StatusCode}) {text}");
                var update = Request(HttpMethod.Put, $"bucket/{options.Bucket}");
                update.Content = JsonContent.Create(settings);
                using var upd = await http.SendAsync(update, ct);
                if (!upd.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Supabase Storage: cannot update bucket ({(int)upd.StatusCode})");
            }
            bucketReady = true;
        }
        finally
        {
            bucketLock.Release();
        }
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        var req = Request(HttpMethod.Post, $"object/{ObjectPath(key)}");
        req.Content = new ByteArrayContent(content);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        req.Headers.Add("x-upsert", "true");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Supabase Storage: cannot save {key} ({(int)res.StatusCode}) {await res.Content.ReadAsStringAsync(ct)}");
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Request(HttpMethod.Get, $"object/authenticated/{ObjectPath(key)}"), ct);
        // Supabase answers 400 (not 404) for a missing object.
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Request(HttpMethod.Delete, $"object/{ObjectPath(key)}"), ct);
        if (!res.IsSuccessStatusCode && res.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.BadRequest))
            res.EnsureSuccessStatusCode();
    }

    /// <summary>A URL the browser can PUT one file to (Supabase keeps it valid for 2 hours).</summary>
    public async Task<string?> CreateUploadUrlAsync(string key, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        using var res = await http.SendAsync(Request(HttpMethod.Post, $"object/upload/sign/{ObjectPath(key)}"), ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        var url = json?["url"]?.GetValue<string>() ?? throw new InvalidOperationException("Supabase Storage: no upload URL");
        return $"{options.Url.TrimEnd('/')}/storage/v1/{url.TrimStart('/')}";
    }
}
