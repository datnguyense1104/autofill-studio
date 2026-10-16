using System.Text;
using System.Text.Json;
using AutoFill.Core;
using AutoFill.Documents.Ingest;
using static AutoFill.Documents.Templates.SchemaBuilder;

namespace AutoFill.Documents.Runs;

public sealed record RecipeCard(Guid Id, string Name, string UseWhen);

public sealed record RecipeSuggestion(Guid RecipeId, string Name, double Score, string Reason);

/// <summary>Which recipes fit new sources: the AI compares the start of each source with every recipe's "dùng khi nào".</summary>
public sealed class RecipeSuggester(IAiClient ai)
{
    public const int CharsPerSource = 6000;

    const string Instructions = """
        Bạn chọn công thức (mẫu văn bản) phù hợp nhất để xử lý bộ tài liệu nguồn vừa nhận.
        Mỗi công thức có tên và mô tả "dùng khi nào". Chọn tối đa 3 công thức phù hợp, xếp từ phù hợp nhất.
        score: 0..1. reason: một câu tiếng Việt, nêu căn cứ trong tài liệu nguồn.
        Không có công thức nào phù hợp thì trả về danh sách rỗng.
        """;

    sealed record Reply(List<Pick> Suggestions);
    sealed record Pick(string RecipeId, double Score, string Reason);

    public async Task<IReadOnlyList<RecipeSuggestion>> SuggestAsync(IReadOnlyList<RunSource> sources, IReadOnlyList<RecipeCard> recipes, CancellationToken ct = default)
    {
        if (recipes.Count == 0) return [];
        var prompt = new StringBuilder("# Công thức\n");
        foreach (var r in recipes) prompt.Append($"- {r.Id}: {r.Name} — dùng khi: {(string.IsNullOrWhiteSpace(r.UseWhen) ? "(chưa mô tả)" : r.UseWhen.Trim())}\n");
        prompt.Append("\n# Tài liệu nguồn (phần đầu)\n");
        foreach (var s in sources) prompt.Append($"\n## {s.Name}\n").Append(SourceText.Render(s.Document, CharsPerSource).Text);

        var schema = Obj(("suggestions", Arr(Obj(("recipeId", Enum(recipes.Select(r => r.Id.ToString()))), ("score", Num()), ("reason", Str())))));
        var node = await ai.GenerateJsonAsync(new AiRequest("recipe-suggest", Instructions, prompt.ToString(), "recipe_suggestions", schema), ct);
        var reply = node.Deserialize<Reply>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (reply?.Suggestions ?? [])
            .Select(p => (p, recipe: recipes.FirstOrDefault(r => r.Id.ToString() == p.RecipeId)))
            .Where(x => x.recipe is not null)
            .DistinctBy(x => x.recipe!.Id)
            .OrderByDescending(x => x.p.Score)
            .Take(3)
            .Select(x => new RecipeSuggestion(x.recipe!.Id, x.recipe.Name, Math.Clamp(x.p.Score, 0, 1), x.p.Reason.Trim()))
            .ToList();
    }
}
