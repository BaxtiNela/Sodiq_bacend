using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WA.Backend.Services;

/// <summary>
/// llama.cpp server (OpenAI-compatible /v1/chat/completions) klienti.
/// Avvalgi Ollama /api/chat o'rniga ishlatiladi — 20-40% tezroq.
/// </summary>
public class OllamaBackendClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private const string SystemPrompt = """
        Sen Windows kompyuterni mustaqil boshqaruvchi AI agentsan. Egangning shaxsiy yordamchisi sifatida:
        - Har qanday vazifani tool orqali O'ZING bajar (buyruq yozmaysan)
        - Qilingan amallarni tushuntir
        - Xatolardan o'rganib, muqobil yechim top
        - Egangni taniysan, uning odatlarini bilyasan
        - Xotiradan foydalanib, avvalgi suhbatlarni eslaysan
        QOIDA: Hech qachon "CMDda qiling" yoki "bajarib ko'ring" dema — doim O'ZING qil.
        """;

    public OllamaBackendClient(IConfiguration config)
    {
        // LLM_URL — llama.cpp server; fallback Ollama URL for backward compat
        _baseUrl = config["LLM_URL"]
                ?? config["OLLAMA_URL"]
                ?? "http://localhost:8080";
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public string DefaultModel => "qwen2.5-7b";

    // ── Non-streaming chat with optional tools ──
    public async Task<OllamaResponse> ChatAsync(
        List<OllamaMessage> messages,
        List<ToolDefinition>? tools = null,
        string? model = null,
        CancellationToken ct = default)
    {
        var body = new
        {
            model    = model ?? DefaultModel,
            messages = messages.Select(SerializeMessage),
            tools    = tools != null && tools.Count > 0 ? (object)tools : null,
            stream   = false,
            max_tokens = 2048
        };

        var resp = await _http.PostAsJsonAsync($"{_baseUrl}/v1/chat/completions", body, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);

        return ParseResponse(json!);
    }

    // ── Streaming tokens (SSE format) ──
    public async IAsyncEnumerable<string> StreamAsync(
        List<OllamaMessage> messages,
        string? model = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model    = model ?? DefaultModel,
            messages = messages.Select(SerializeMessage),
            stream   = true,
            max_tokens = 2048
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            // SSE format: "data: {...}" or "data: [DONE]"
            if (!line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            var node  = JsonNode.Parse(data);
            var token = node?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
    }

    // ── Fact extraction for LearningService ──
    public async Task<string> ExtractFactsAsync(
        string userMsg, string assistantMsg, CancellationToken ct = default)
    {
        var messages = new List<OllamaMessage>
        {
            new("system", """
                Suhbatdan foydalanuvchi haqida quyidagilarni chiqar:
                1. Faktlar (ism, kasb, joylashuv, texnologiyalar)
                2. Odatlar (tez-tez qiladigan narsalar)
                3. Afzalliklar (nima yoqadi, nima yoqmaydi)

                Faqat JSON qaytir, boshqa hech narsa yozma:
                {"facts":[{"key":"...","value":"...","category":"fact|habit|preference"}],"habits":[]}

                Agar hech narsa yo'q bo'lsa: {"facts":[],"habits":[]}
                """),
            new("user", $"Foydalanuvchi: {userMsg}\nAssistent: {assistantMsg}")
        };

        var resp = await ChatAsync(messages, model: DefaultModel, ct: ct);
        return resp.Content;
    }

    public OllamaMessage BuildSystemMessage(string contextBlock) =>
        new("system", $"{SystemPrompt}\n\n{contextBlock}");

    // ── Serialize message to OpenAI format (handles tool + assistant-with-tool_calls) ──
    private static object SerializeMessage(OllamaMessage m)
    {
        if (m.Role == "tool")
            return new { role = m.Role, content = m.Content, tool_call_id = m.ToolCallId ?? string.Empty };

        if (m.ToolCallsJson != null)
            return new { role = m.Role, content = (string?)null,
                tool_calls = JsonNode.Parse(m.ToolCallsJson) };

        return new { role = m.Role, content = m.Content };
    }

    // ── Parse OpenAI-compatible response ──
    private static OllamaResponse ParseResponse(JsonNode json)
    {
        // OpenAI format: choices[0].message
        var msg     = json["choices"]?[0]?["message"];
        var content = msg?["content"]?.GetValue<string>() ?? string.Empty;
        var toolCalls = new List<OllamaToolCall>();

        var rawCalls = msg?["tool_calls"]?.AsArray();
        if (rawCalls != null)
        {
            foreach (var tc in rawCalls)
            {
                var fn = tc?["function"];
                if (fn == null) continue;
                toolCalls.Add(new OllamaToolCall(
                    fn["name"]?.GetValue<string>() ?? string.Empty,
                    fn["arguments"]?.GetValue<string>()
                        ?? fn["arguments"]?.ToJsonString()
                        ?? "{}",
                    tc?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N")[..8]));
            }
        }

        // JSON fallback (model ba'zan content ichiga yozadi)
        if (toolCalls.Count == 0 && content.TrimStart().StartsWith("{\"name\""))
        {
            try
            {
                var node = JsonNode.Parse(content.Trim());
                var name = node?["name"]?.GetValue<string>();
                var args = node?["parameters"]?.ToJsonString()
                        ?? node?["arguments"]?.ToJsonString()
                        ?? "{}";
                if (!string.IsNullOrEmpty(name))
                    toolCalls.Add(new OllamaToolCall(name, args));
            }
            catch { }
        }

        return new OllamaResponse(content, toolCalls);
    }
}

// ---- Value types ----

public record OllamaMessage(string Role, string Content,
    string? ToolCallId = null, string? ToolCallsJson = null);

public record OllamaResponse(string Content, List<OllamaToolCall> ToolCalls)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
}

public record OllamaToolCall(string Name, string ArgsJson, string Id = "");

public record ToolDefinition(string Type, ToolFunction Function);

public record ToolFunction(
    string Name,
    string Description,
    ToolParameters Parameters);

public record ToolParameters(
    string Type,
    Dictionary<string, ToolProperty> Properties,
    List<string> Required);

public record ToolProperty(string Type, string Description, string[]? Enum = null);
