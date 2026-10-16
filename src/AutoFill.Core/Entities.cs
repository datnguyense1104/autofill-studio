namespace AutoFill.Core;

/// <summary>Rows that belong to one workspace; the database layer filters them by the current workspace.</summary>
public interface IWorkspaceOwned
{
    Guid WorkspaceId { get; set; }
}

public enum UserStatus { Pending, Active, Disabled }

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Lower-cased, trimmed.</summary>
    public required string Email { get; set; }
    public string Name { get; set; } = "";
    public required string PasswordHash { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Pending;
    /// <summary>Platform administrator: activates accounts. Not a workspace role.</summary>
    public bool IsAdmin { get; set; }
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum WorkspaceRole { Owner, Editor, Reviewer }

public sealed class Membership
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public WorkspaceRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A file kept in the file store. Workspace files are named by content (sha256), so they never change.</summary>
public sealed class StoredFile : IWorkspaceOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Key { get; set; }
    /// <summary>The name the user gave the file (may contain Vietnamese letters; the key never does).</summary>
    public required string Name { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Sha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A source file already read into a normalized document (JSON), kept per workspace and file
/// content, so the same file is never sent to the AI twice.
/// </summary>
public sealed class Transcript : IWorkspaceOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Sha256 { get; set; }
    /// <summary>The reader version that produced <see cref="Document"/>; an older version is read again.</summary>
    public int ReaderVersion { get; set; }
    public required string Document { get; set; }
    public int Pages { get; set; }
    public int AiPages { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum JobStatus { Queued, Running, Done, Failed }

/// <summary>Background work (one recipe run, one template analysis...). Payload/progress/result are JSON.</summary>
public sealed class Job : IWorkspaceOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid? CreatedBy { get; set; }
    public required string Type { get; set; }
    public string Payload { get; set; } = "{}";
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int Attempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    /// <summary>Latest progress note shown to the user, e.g. {"step":"Đọc nguồn","done":1,"total":3}.</summary>
    public string? Progress { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A recipe: a template and how each of its slots gets a value. Its content lives in versions.</summary>
public sealed class Recipe : IWorkspaceOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    /// <summary>"Dùng khi nào": what sources this recipe answers (used to suggest a recipe for new sources).</summary>
    public string UseWhen { get; set; } = "";
    public int CurrentVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A saved state of a recipe. Never changed once saved, so a past run always shows what it used.</summary>
public sealed class RecipeVersion : IWorkspaceOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid RecipeId { get; set; }
    public int Number { get; set; }
    public Guid TemplateFileId { get; set; }
    /// <summary>"word" | "excel"</summary>
    public required string Kind { get; set; }
    /// <summary>JSON array of slots (see AutoFill.Documents.Templates.Slot).</summary>
    public required string Slots { get; set; }
    /// <summary>JSON array of extra checks (see AutoFill.Documents.Runs.RecipeRule).</summary>
    public string Rules { get; set; } = "[]";
    /// <summary>Instructions given to the AI on every call of a run.</summary>
    public string Notes { get; set; } = "";
    public bool CheckMissing { get; set; } = true;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum RunStatus { Queued, Running, NeedsReview, Done }

/// <summary>Sources + a recipe version → values to review, then export.</summary>
public sealed class Run : IWorkspaceOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid RecipeVersionId { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    /// <summary>JSON array of file ids, in the order given.</summary>
    public string SourceFileIds { get; set; } = "[]";
    public Guid? JobId { get; set; }
    /// <summary>JSON array of information the sources have and no slot carries.</summary>
    public string Missing { get; set; } = "[]";
    /// <summary>JSON array of notes for the reviewer (text cut, ...).</summary>
    public string Notes { get; set; } = "[]";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One slot's value in a run: what was found, its proof, and what the reviewer changed.</summary>
public sealed class RunValue : IWorkspaceOwned
{
    public Guid RunId { get; set; }
    public required string SlotId { get; set; }
    public Guid WorkspaceId { get; set; }
    /// <summary>JSON SlotValue as filled.</summary>
    public required string Value { get; set; }
    /// <summary>JSON array of evidence (source index, page, quote, found).</summary>
    public string Evidence { get; set; } = "[]";
    public string Warnings { get; set; } = "[]";
    public double Confidence { get; set; }
    /// <summary>"ai" | "rule" | "none"</summary>
    public required string Origin { get; set; }
    public string? Note { get; set; }
    /// <summary>JSON SlotValue the reviewer set; null = the filled value stands.</summary>
    public string? ReviewedValue { get; set; }
    public bool Reviewed { get; set; }
}

/// <summary>One AI call: what it cost and how long it took.</summary>
public sealed class AiLog
{
    public long Id { get; set; }
    public Guid? WorkspaceId { get; set; }
    /// <summary>The background job the call was made for (a run's cost is the sum over its job).</summary>
    public Guid? JobId { get; set; }
    public required string Purpose { get; set; }
    public required string Model { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int DurationMs { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
