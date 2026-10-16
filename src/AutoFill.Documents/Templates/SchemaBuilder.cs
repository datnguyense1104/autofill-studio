using System.Text.Json.Nodes;

namespace AutoFill.Documents.Templates;

/// <summary>Short helpers for strict JSON schemas (every property required, no extra properties).</summary>
static class SchemaBuilder
{
    public static JsonObject Str() => new() { ["type"] = "string" };
    public static JsonObject Int() => new() { ["type"] = "integer" };
    public static JsonObject Num() => new() { ["type"] = "number" };
    public static JsonObject Enum(IEnumerable<string> values) => new() { ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]) };
    public static JsonObject Arr(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

    public static JsonObject Obj(params (string Name, JsonObject Schema)[] properties) => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray([.. properties.Select(p => (JsonNode)p.Name)]),
        ["properties"] = new JsonObject(properties.Select(p => KeyValuePair.Create(p.Name, (JsonNode?)p.Schema))),
    };

    public static string[] Names<T>() where T : struct, System.Enum => [.. System.Enum.GetNames<T>().Select(n => n.ToLowerInvariant())];

    public static T Parse<T>(string? value, T fallback) where T : struct, System.Enum =>
        System.Enum.TryParse<T>(value, ignoreCase: true, out var v) ? v : fallback;
}
