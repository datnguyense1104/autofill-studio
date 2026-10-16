using System.ClientModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AutoFill.Core;
using OpenAI;
using OpenAI.Chat;

namespace AutoFill.Ai;

/// <param name="BaseUrl">empty for api.openai.com; set it for Azure OpenAI or another compatible endpoint</param>
public sealed record AiOptions(string? ApiKey, string Model, string? BaseUrl);

/// <summary>Writes one row per AI call (cost, time, error). Implemented by the host, which owns the database.</summary>
public interface IAiUsageLog
{
    Task WriteAsync(AiLog entry, CancellationToken ct = default);
}

/// <summary>
/// Chat Completions with a strict JSON schema: the model can only answer with JSON of that
/// shape. Files (PDF scans, images) travel inside the user message.
/// </summary>
public sealed class OpenAiJsonClient(AiOptions options, IAiUsageLog usage, IWorkspaceScope scope) : IAiClient
{
    ChatClient Client()
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new UserFacingException("Máy chủ chưa được cấu hình AI (thiếu OPENAI_API_KEY)", 503);
        var clientOptions = new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(options.BaseUrl)) clientOptions.Endpoint = new Uri(options.BaseUrl);
        return new ChatClient(options.Model, new ApiKeyCredential(options.ApiKey), clientOptions);
    }

    public async Task<JsonNode> GenerateJsonAsync(AiRequest request, CancellationToken ct = default)
    {
        var parts = new List<ChatMessageContentPart>();
        foreach (var file in request.Files ?? [])
        {
#pragma warning disable OPENAI001 // PDF input is marked "evaluation" in the SDK, but it is the documented way to send PDFs.
            parts.Add(file.ContentType.StartsWith("image/", StringComparison.Ordinal)
                ? ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(file.Content), file.ContentType)
                : ChatMessageContentPart.CreateFilePart(BinaryData.FromBytes(file.Content), file.ContentType, file.Name));
#pragma warning restore OPENAI001
        }
        parts.Add(ChatMessageContentPart.CreateTextPart(request.Input));

        ChatMessage[] messages = [new SystemChatMessage(request.Instructions), new UserChatMessage(parts)];
        var chatOptions = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                request.SchemaName, BinaryData.FromString(request.Schema.ToJsonString()), jsonSchemaIsStrict: true),
        };

        var log = new AiLog { WorkspaceId = scope.WorkspaceId, JobId = scope.JobId, Purpose = request.Purpose, Model = options.Model };
        var watch = Stopwatch.StartNew();
        try
        {
            ChatCompletion completion = await Client().CompleteChatAsync(messages, chatOptions, ct);
            log.InputTokens = completion.Usage?.InputTokenCount ?? 0;
            log.OutputTokens = completion.Usage?.OutputTokenCount ?? 0;
            if (completion.Refusal is { Length: > 0 } refusal) throw new UserFacingException($"AI từ chối xử lý: {refusal}", 502);
            var text = completion.Content.FirstOrDefault()?.Text ?? throw new InvalidOperationException("AI returned no content");
            return JsonNode.Parse(text) ?? throw new InvalidOperationException("AI returned empty JSON");
        }
        catch (Exception err) when (err is not OperationCanceledException)
        {
            log.Error = err.Message.Length > 500 ? err.Message[..500] : err.Message;
            throw;
        }
        finally
        {
            log.DurationMs = (int)watch.ElapsedMilliseconds;
            await usage.WriteAsync(log, CancellationToken.None);
        }
    }
}
