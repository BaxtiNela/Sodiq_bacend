using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WA.Agent.Services;

namespace WA.App.Services;

/// <summary>
/// OpenAI-compatible API (DeepSeek, OpenAI) — streaming + tool calling bilan.
/// DeepSeek agent loop: tool call → WPF executor → natija → davom etish.
/// </summary>
public class ExternalApiClient
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static bool IsExternalModel(string model) =>
        model.StartsWith("deepseek") || model.StartsWith("gpt-") ||
        model.StartsWith("o1") || model.StartsWith("o3");

    public static string BaseUrlFor(string model) =>
        model.StartsWith("deepseek")
            ? "https://api.deepseek.com/v1/chat/completions"
            : "https://api.openai.com/v1/chat/completions";

    public static string ProviderLabel(string model) =>
        model.StartsWith("deepseek") ? "DeepSeek" : "OpenAI";

    // ─── Simple streaming (tools yo'q, oddiy suhbat uchun) ───────────────────

    public async Task StreamAsync(
        string model,
        string apiKey,
        List<(string Role, string Content)> messages,
        Action<string> onToken,
        CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model,
            stream   = true,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        });

        using var resp = await PostAsync(BaseUrlFor(model), apiKey, body, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null || !line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var cp) && !string.IsNullOrEmpty(cp.GetString()))
                    onToken(cp.GetString()!);
            }
            catch { }
        }
    }

    // ─── Agent call with tools (non-streaming, tool_calls parse) ─────────────

    /// <summary>
    /// DeepSeek ga toollar bilan so'rov yuboradi.
    /// Qaytaradi: (Content, ToolCalls) — biri null bo'ladi.
    /// </summary>
    public async Task<AgentTurnResult> ChatWithToolsAsync(
        string model,
        string apiKey,
        List<JsonObject> messages,
        CancellationToken ct = default)
    {
        // Toollarni OpenAI formatida serialize qilish
        var toolDefs = ToolExecutor.GetAllTools();
        var tools    = toolDefs.Select(t => new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"]        = t.Name,
                ["description"] = t.Description,
                ["parameters"]  = JsonNode.Parse(JsonSerializer.Serialize(t.Parameters))
            }
        }).ToList();

        var bodyNode = new JsonObject
        {
            ["model"]    = model,
            ["stream"]   = false,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["tools"]    = new JsonArray(tools.Select(t => (JsonNode)t).ToArray()),
            ["tool_choice"] = "auto",
            ["max_tokens"]  = 2048
        };

        using var resp = await PostAsync(BaseUrlFor(model), apiKey, bodyNode.ToJsonString(), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"DeepSeek {(int)resp.StatusCode}: {err[..Math.Min(300, err.Length)]}");
        }

        var json    = await resp.Content.ReadAsStringAsync(ct);
        var doc     = JsonDocument.Parse(json);
        var msg     = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = msg.TryGetProperty("content", out var cp) ? cp.GetString() : null;
        var finish  = doc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString();

        if (finish == "tool_calls" && msg.TryGetProperty("tool_calls", out var tcArr))
        {
            var calls = tcArr.EnumerateArray().Select(tc => new DeepSeekToolCall(
                tc.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N")[..8],
                tc.GetProperty("function").GetProperty("name").GetString() ?? "",
                tc.GetProperty("function").GetProperty("arguments").GetString() ?? "{}"
            )).ToList();

            // assistant xabarini saqlash uchun raw JSON
            var assistantNode = JsonNode.Parse(json)!["choices"]![0]!["message"]!.DeepClone()!.AsObject();

            return new AgentTurnResult(null, calls, assistantNode);
        }

        return new AgentTurnResult(content ?? "", null, null);
    }

    // ─── Helper ──────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> PostAsync(string url, string apiKey, string json, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
    }
}

// ── Result types ──────────────────────────────────────────────────────────────

public record DeepSeekToolCall(string Id, string Name, string ArgsJson);

public record AgentTurnResult(
    string?                  Content,
    List<DeepSeekToolCall>?  ToolCalls,
    JsonObject?              AssistantMessageNode)
{
    public bool HasToolCalls => ToolCalls is { Count: > 0 };
}
