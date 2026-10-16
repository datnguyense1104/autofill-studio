using System.Text.Json.Nodes;

namespace AutoFill.Core;

/// <summary>The workspace the current request (or background job) works in.</summary>
public interface IWorkspaceScope
{
    Guid? WorkspaceId { get; }
    /// <summary>The background job being run, if any (AI calls are logged against it).</summary>
    Guid? JobId { get; }
}

/// <summary>Settable scope: set per request by the auth middleware, per job by the worker.</summary>
public sealed class WorkspaceScope : IWorkspaceScope
{
    public Guid? WorkspaceId { get; set; }
    public Guid? JobId { get; set; }
}

/// <summary>Raw file bytes by key. Keys are ASCII paths such as <c>ws/{workspace}/{sha256}.pdf</c>.</summary>
public interface IFileStore
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default);
    /// <returns>null when the key does not exist</returns>
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    /// <summary>A short-lived URL the browser can PUT one file to; null when uploads must go through the server.</summary>
    Task<string?> CreateUploadUrlAsync(string key, CancellationToken ct = default);
}

/// <summary>A file sent to the AI along with the prompt (PDF, image).</summary>
public sealed record AiFile(string Name, string ContentType, byte[] Content);

public sealed record AiRequest(
    string Purpose,
    string Instructions,
    string Input,
    string SchemaName,
    JsonObject Schema,
    IReadOnlyList<AiFile>? Files = null);

/// <summary>Structured AI calls: the answer always matches the JSON schema given.</summary>
public interface IAiClient
{
    Task<JsonNode> GenerateJsonAsync(AiRequest request, CancellationToken ct = default);
}

/// <summary>Runs one type of background job. Registered by the phase that owns the job type.</summary>
public interface IJobHandler
{
    string Type { get; }
    /// <returns>the job result as JSON (stored on the job)</returns>
    Task<string?> RunAsync(Job job, IJobProgress progress, CancellationToken ct);
}

public interface IJobProgress
{
    Task ReportAsync(string step, int? done = null, int? total = null);
}

/// <summary>An error whose message is meant for the user (Vietnamese), returned as HTTP 400 unless Status says otherwise.</summary>
public class UserFacingException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
