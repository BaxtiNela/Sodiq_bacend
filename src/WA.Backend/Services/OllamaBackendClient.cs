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

    // camelCase — tools record larini to'g'ri serialize qilish uchun
    private static readonly JsonSerializerOptions CamelOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const string SystemPrompt = """
        Sen Windows kompyuterni MUSTAQIL boshqaruvchi AI agentsan. Cursor va Warp kabi eng aqlli agentlar darajasida ishlaysan.

        FIKRLASH TARTIBI (ReAct):
        1. TAHLIL — Vazifani tushun: nima kerak? Qaysi tool eng mos?
        2. HARAKAT — Tool ishlatib vazifani boshlang
        3. KUZAT — Natijani ko'r, muvaffaqiyatli bo'ldimi? Xato bo'lsa tuzat
        4. YAKUN — Foydalanuvchiga faqat foydali natijani qisqa yoz

        QATTIQ QOIDALAR:
        - HECH QACHON "Siz qiling", "bosing", "CMDda yozing", "o'zingiz qiling" dema — O'ZING qil
        - Har qanday vazifani DARHOL tool orqali bajar
        - Bir maqsad uchun bir nechta tool ketma-ket ishlatishdan qo'rqma
        - Xato bo'lsa: muqobil yo'l bil, qayta ur, baribir natija chiqar
        - Javobdan oldin o'ylama — harakat qil, natijasini ko'r

        TOOL TANLASH QOIDALARI:
        - Internet, web ma'lumot, yangilik → web_search(query) keyin read_url(url)
        - Oddiy veb sayt ochish → run_command("start chrome URL")
        - Fayl o'qish → read_file(path)
        - Fayl yozish → write_file(path, content)
        - Papka → list_directory(path)
        - Dastur → open_app(name) yoki run_command("start app")
        - Tizim → get_system_info()
        - Screenshot → take_screenshot()
        - Clipboard → get_clipboard() / set_clipboard(text)
        - Ochiq oynalar → list_windows()
        - Vaqt → get_time()
        - Ovoz → set_volume(level)
        - Fayl o'chirish → delete_file(path)
        - Fayl ko'chirish/nomlash → rename_file(old, new)

        INTERNET QIDIRUV MISOLI:
        User: "Python 3.13 yangiliklari nima?"
        → web_search("Python 3.13 new features release notes")
        → read_url(birinchi natija URL)
        → Xulosa chiqar va user ga ko'rsat

        DESKTOP ISHLASH MISOLLARI:
        - "screenshot ol" → take_screenshot()
        - "clipboardda nima bor" → get_clipboard()
        - "ovozni 50% qil" → set_volume("50")
        - "vaqt necha" → get_time()
        - "ochiq dasturlar" → list_windows()
        - "notepadni yop" → close_window("Notepad")
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
        var body = BuildRequestNode(model ?? DefaultModel, messages, tools, stream: false);
        var reqJson = body.ToJsonString();
        var content = new StringContent(reqJson, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{_baseUrl}/v1/chat/completions", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"LLM {(int)resp.StatusCode}: {errBody}\n\nREQ={reqJson[..Math.Min(500,reqJson.Length)]}");
        }
        var json = await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        return ParseResponse(json!);
    }

    // ── Streaming tokens (SSE format) ──
    public async IAsyncEnumerable<string> StreamAsync(
        List<OllamaMessage> messages,
        string? model = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var bodyNode = BuildRequestNode(model ?? DefaultModel, messages, null, stream: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(bodyNode.ToJsonString(), Encoding.UTF8, "application/json")
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
                Suhbatdan faqat qayta ishlatilishi mumkin bo'lgan muhim ma'lumotlarni chiqar.
                Umumiy salomlashish, "ok", "rahmat" kabi narsalarni SAQLAМА.

                Kategoriyalar:
                - fact: shaxsiy ma'lumot (ism, kasb, shahar, texnologiya)
                - project: loyiha nomi + qisqacha tavsif (masalan: "quvna_app": "iOS/Android audio ilovasi, DSD kompaniyasi")
                - instruction: kelajakda ham amal qilinishi kerak bo'lgan qoida ("har doim TypeScript ishlat", "commit oldidan test yoz")
                - work_style: ish uslubi va rol ("fullstack dasturchi", "agile metodologiya", "solo founder")
                - preference: nima yoqadi/yoqmaydi
                - habit: tez-tez so'raladigan narsalar
                - success: nima ishladi, qanday yechildi (juda qisqa)
                - failure: nima ishlamadi, qanday xato bo'ldi (juda qisqa)

                Faqat JSON, boshqa narsa yozma:
                {"facts":[{"key":"...","value":"...","category":"fact|project|instruction|work_style|preference|habit|success|failure"}],"habits":[]}

                Agar hech narsa yo'q: {"facts":[],"habits":[]}
                """),
            new("user", $"Foydalanuvchi: {userMsg}\nAssistent: {assistantMsg}")
        };

        var resp = await ChatAsync(messages, model: DefaultModel, ct: ct);
        return resp.Content;
    }

    public OllamaMessage BuildSystemMessage(string contextBlock) =>
        new("system", $"{SystemPrompt}\n\n{contextBlock}");

    // ── Build full request body as JsonNode (avoids CamelCase/object serialization issues) ──
    private static JsonNode BuildRequestNode(string model, List<OllamaMessage> messages,
        List<ToolDefinition>? tools, bool stream)
    {
        var msgArray = new JsonArray();
        foreach (var m in messages)
            msgArray.Add(SerializeMessageNode(m));

        var body = new JsonObject
        {
            ["model"]              = model,
            ["messages"]           = msgArray,
            ["stream"]             = stream,
            ["max_tokens"]         = 2048,
            ["temperature"]        = 0.75,
            ["repeat_penalty"]     = 1.15,
            ["top_p"]              = 0.92,
            ["frequency_penalty"]  = 0.1
        };

        if (tools != null && tools.Count > 0)
        {
            var toolArray = new JsonArray();
            foreach (var t in tools)
                toolArray.Add(new JsonObject
                {
                    ["type"] = t.Type,
                    ["function"] = new JsonObject
                    {
                        ["name"]        = t.Function.Name,
                        ["description"] = t.Function.Description,
                        ["parameters"]  = new JsonObject
                        {
                            ["type"]       = t.Function.Parameters.Type,
                            ["properties"] = BuildPropertiesNode(t.Function.Parameters.Properties),
                            ["required"]   = new JsonArray(t.Function.Parameters.Required
                                                .Select(r => (JsonNode)JsonValue.Create(r)!).ToArray())
                        }
                    }
                });
            body["tools"] = toolArray;
        }

        return body;
    }

    private static JsonObject BuildPropertiesNode(Dictionary<string, ToolProperty> props)
    {
        var obj = new JsonObject();
        foreach (var (key, p) in props)
        {
            var propNode = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Enum != null)
                propNode["enum"] = new JsonArray(p.Enum.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
            obj[key] = propNode;
        }
        return obj;
    }

    private static JsonNode SerializeMessageNode(OllamaMessage m)
    {
        var obj = new JsonObject { ["role"] = m.Role };

        if (m.Role == "tool")
        {
            obj["content"]      = m.Content;
            obj["tool_call_id"] = m.ToolCallId ?? string.Empty;
            return obj;
        }

        if (m.ToolCallsJson != null)
        {
            obj["content"]    = (JsonNode?)null;
            obj["tool_calls"] = JsonNode.Parse(m.ToolCallsJson);
            return obj;
        }

        obj["content"] = m.Content;
        return obj;
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
