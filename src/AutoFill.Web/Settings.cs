namespace AutoFill.Web;

/// <summary>Everything the app reads from the environment (or a .env file next to it).</summary>
public sealed record Settings
{
    public required string DatabaseUrl { get; init; }
    public required string FileStore { get; init; } // "supabase" | "disk"
    public required string DataDir { get; init; }
    public string? SupabaseUrl { get; init; }
    public string? SupabaseServiceKey { get; init; }
    public required string SupabaseBucket { get; init; }
    public long MaxFileBytes { get; init; }
    public string? OpenAiApiKey { get; init; }
    public required string OpenAiModel { get; init; }
    public string? OpenAiBaseUrl { get; init; }
    /// <summary>The account registered with this email becomes the active platform administrator.</summary>
    public string? AdminEmail { get; init; }
    public int Workers { get; init; }
    public bool CookieSecure { get; init; }
    /// <summary>How much of a source document's text goes into a prompt; the user is warned when it is cut.</summary>
    public int MaxSourceChars { get; init; }
    /// <summary>LibreOffice's soffice for .doc/.xls; null = look in the usual places.</summary>
    public string? SofficePath { get; init; }
    /// <summary>
    /// The path the app is published under ("/auto-fill" for https://ntdat.qzz.io/auto-fill/), or ""
    /// at the site root. Cloudflare Tunnel passes the path on unchanged, so the app removes it.
    /// </summary>
    public string BasePath { get; init; } = "";

    public static Settings From(IConfiguration c, bool isDevelopment)
    {
        static string? Value(IConfiguration c, string key) => c[key] is { Length: > 0 } v ? v.Trim() : null;
        var supabaseUrl = Value(c, "SUPABASE_URL");
        var store = Value(c, "FILE_STORE") ?? (supabaseUrl is null ? "disk" : "supabase");
        var settings = new Settings
        {
            DatabaseUrl = Value(c, "DATABASE_URL") ?? throw new InvalidOperationException("DATABASE_URL is not set (see .env.example)"),
            FileStore = store,
            DataDir = Value(c, "DATA_DIR") ?? "data",
            SupabaseUrl = supabaseUrl,
            SupabaseServiceKey = Value(c, "SUPABASE_SERVICE_KEY"),
            SupabaseBucket = Value(c, "SUPABASE_BUCKET") ?? "autofill",
            MaxFileBytes = (long.TryParse(Value(c, "MAX_FILE_MB"), out var mb) ? mb : 50) * 1024 * 1024,
            OpenAiApiKey = Value(c, "OPENAI_API_KEY"),
            OpenAiModel = Value(c, "OPENAI_MODEL") ?? "gpt-6-luna",
            OpenAiBaseUrl = Value(c, "OPENAI_BASE_URL"),
            AdminEmail = Value(c, "ADMIN_EMAIL")?.ToLowerInvariant(),
            Workers = int.TryParse(Value(c, "WORKERS"), out var w) ? Math.Clamp(w, 0, 16) : 2,
            CookieSecure = bool.TryParse(Value(c, "COOKIE_SECURE"), out var secure) ? secure : !isDevelopment,
            MaxSourceChars = int.TryParse(Value(c, "MAX_SOURCE_CHARS"), out var chars) ? Math.Max(chars, 1000) : 120_000,
            SofficePath = Value(c, "SOFFICE_PATH"),
            BasePath = Value(c, "BASE_PATH") is { } basePath ? "/" + basePath.Trim('/') : "",
        };
        if (settings.BasePath.Length > 1 && !System.Text.RegularExpressions.Regex.IsMatch(settings.BasePath, "^(/[A-Za-z0-9._-]+)+$"))
            throw new InvalidOperationException("BASE_PATH must look like /auto-fill");
        if (settings.BasePath == "/") settings = settings with { BasePath = "" };
        if (settings.FileStore == "supabase" && (settings.SupabaseUrl is null || settings.SupabaseServiceKey is null))
            throw new InvalidOperationException("FILE_STORE=supabase needs SUPABASE_URL and SUPABASE_SERVICE_KEY");
        if (settings.FileStore is not ("supabase" or "disk"))
            throw new InvalidOperationException("FILE_STORE must be 'supabase' or 'disk'");
        return settings;
    }
}

/// <summary>Loads KEY=VALUE lines from a .env file into the process environment, without overriding real variables.</summary>
public static class EnvFile
{
    /// <summary>The nearest .env in this folder or a parent (dotnet run starts in the project folder, the file lives at the repo root).</summary>
    public static string? Find(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".env");
            if (File.Exists(path)) return path;
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) break;
        }
        return null;
    }

    public static void Load(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (key.StartsWith("export ", StringComparison.Ordinal)) key = key[7..].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
        }
    }
}
