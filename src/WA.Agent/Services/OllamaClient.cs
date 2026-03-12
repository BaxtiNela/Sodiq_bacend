using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WA.Agent.Models;

namespace WA.Agent.Services;

/// <summary>Ollama HTTP API client - tool calling bilan</summary>
public class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    public string Model { get; set; }

    private static readonly string SystemPrompt = """
        Sen Windows kompyuterni mustaqil boshqaruvchi AI agentsan.

        ASOSIY QOIDALAR:
        1. Foydalanuvchi biror narsa so'rasa — SEN O'ZING QIL.
        2. Hech qachon "CMDda qiling" yoki "terminalda bajaring" dema.
        3. run_command, run_powershell yoki boshqa toollarni O'ZING ishlatib vazifani bajar.
        4. Natijani ko'r, xato bo'lsa tuzat va qayta urin.
        5. O'rgangan muhim narsalarni save_memory bilan saqla.
        6. O'zbek tilida qisqa va aniq javob ber.
        """;

    public OllamaClient(string model = "qwen2.5:7b", string baseUrl = "http://localhost:11434")
    {
        Model = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/api/tags");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<string>> GetModelsAsync()
    {
        try
        {
            var resp = await _http.GetFromJsonAsync<JsonElement>($"{_baseUrl}/api/tags");
            return resp.GetProperty("models")
                       .EnumerateArray()
                       .Select(m => m.GetProperty("name").GetString() ?? "")
                       .Where(n => n.Length > 0)
                       .ToList();
        }
        catch { return []; }
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        ConversationHistory history,
        List<ToolDefinition>? tools = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = BuildMessages(history);
        var payload = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["stream"] = true,
        };

        if (tools?.Count > 0)
            payload["tools"] = tools.Select(t => t.ToOllamaFormat()).ToList();

        var json = JsonSerializer.Serialize(payload);

        HttpResponseMessage? resp = null;
        string? earlyError = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            earlyError = $"[XATO] {ex.Message}";
        }

        if (earlyError != null) { yield return earlyError; yield break; }
        if (!resp!.IsSuccessStatusCode) { yield return $"[XATO] HTTP {resp.StatusCode}"; yield break; }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement chunk;
            try { chunk = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }

            var msg = chunk.TryGetProperty("message", out var m) ? m : default;
            if (msg.ValueKind == JsonValueKind.Object)
            {
                var content = msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(content))
                    yield return content;
            }

            if (chunk.TryGetProperty("done", out var done) && done.GetBoolean())
                break;
        }
    }

    public async Task<AgentResponse> ChatAsync(
        ConversationHistory history,
        List<ToolDefinition>? tools = null,
        CancellationToken ct = default)
    {
        var messages = BuildMessages(history);
        var payload = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["stream"] = false,
        };

        if (tools?.Count > 0)
            payload["tools"] = tools.Select(t => t.ToOllamaFormat()).ToList();

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        try
        {
            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return new AgentResponse($"[XATO] HTTP {resp.StatusCode}: {body}");

            var data = JsonSerializer.Deserialize<JsonElement>(body);
            var message = data.GetProperty("message");
            var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            // Tool calls parsing
            List<ToolCall>? toolCalls = null;
            if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            {
                toolCalls = [];
                int idx = 0;
                foreach (var t in tc.EnumerateArray())
                {
                    var func = t.GetProperty("function");
                    var name = func.GetProperty("name").GetString() ?? "";
                    var args = ParseArguments(func.TryGetProperty("arguments", out var a) ? a : default);
                    toolCalls.Add(new ToolCall($"tc_{idx++}", name, args));
                }
            }

            // Fallback: JSON ni contentdan parse qilish
            if (toolCalls == null && !string.IsNullOrWhiteSpace(content))
                toolCalls = TryParseToolFromContent(content);

            return new AgentResponse(content, toolCalls);
        }
        catch (Exception ex)
        {
            return new AgentResponse($"[XATO] {ex.Message}");
        }
    }

    private static Dictionary<string, object?> ParseArguments(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
                dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
        }
        else if (el.ValueKind == JsonValueKind.String)
        {
            try
            {
                var inner = JsonSerializer.Deserialize<JsonElement>(el.GetString()!);
                return ParseArguments(inner);
            }
            catch { }
        }
        return dict;
    }

    private static List<ToolCall>? TryParseToolFromContent(string content)
    {
        // {"name": "...", "arguments": {...}} pattern
        var match = System.Text.RegularExpressions.Regex.Match(
            content, @"\{""name""\s*:\s*""([^""]+)""\s*,\s*""arguments""\s*:(.*?)\}",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        if (!match.Success) return null;

        try
        {
            var name = match.Groups[1].Value;
            var argsJson = match.Groups[2].Value.TrimEnd('}', ' ');
            var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson + "}") ?? [];
            return [new ToolCall("parsed_0", name, args)];
        }
        catch { return null; }
    }

    private static List<object> BuildMessages(ConversationHistory history)
    {
        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt }
        };
        foreach (var msg in history.Messages)
            messages.Add(new { role = msg.Role, content = msg.Content });
        return messages;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Tool definition (OpenAI format)</summary>
public record ToolDefinition(string Name, string Description, Dictionary<string, object> Parameters)
{
    public object ToOllamaFormat() => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = Description,
            parameters = Parameters
        }
    };
}
