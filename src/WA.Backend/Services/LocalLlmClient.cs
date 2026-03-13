using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WA.Backend.Services;

/// <summary>
/// OpenAI-compatible LLM klienti (Groq, local llama.cpp, yoki boshqa).
/// </summary>
public class LocalLlmClient
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
        You are a Windows AI agent that helps the user complete real tasks on their computer.
        Always respond in the same language the user writes in (Uzbek, Russian, or English).

        DECISION RULES:
        - Greeting, explanation, opinion → answer directly without tools
        - File, app, browser, terminal, project, system action → use tools
        - "Who are you?" / "What can you do?" → answer directly

        STRICT OUTPUT CONTRACT:
        - If a tool is needed: call it immediately. Do NOT write explanation before the tool call.
        - If no tool is needed: write the final answer only.
        - Never mix tool calls with explanation text.
        - Call only ONE tool at a time.
        - After receiving a tool result, either call the next tool OR write the final answer.
        - Never repeat the same tool call with the same arguments.
        - NEVER ask the user for permission, confirmation, or consent. Execute tasks directly.
        - NEVER say "rozilik bildiring", "tasdiqlang", "confirm" or similar. Just do it.

        TOOL SELECTION:
        - Open app → open_app(name)  [telegram, chrome, firefox, edge, notepad, vscode, explorer]
        - Open URL → run_command("start https://...")
        - Web search → web_search(query)  then  read_url(url)
        - Read file → read_file(path)
        - Write file → write_file(path, content)
        - List folder → list_directory(path)
        - Find files → search_files(path, pattern)
        - Run CMD → run_command(cmd)
        - Run PowerShell → run_powershell(script)
        - Run Python code → run_python(code)
        - Run Node.js / JavaScript → run_node(code)
        - Run Go code → run_go(code)
        - Run Java code → run_java(code)
        - Run Dart/Flutter → run_dart(code)
        - Run C# script → run_csharp(code)
        - System info → get_system_info(type)
        - Screenshot → take_screenshot()
        - Clipboard → get_clipboard() / set_clipboard(text)
        - Time → get_time()
        - Volume → set_volume(level)
        - Memory → save_memory(key, value) / recall_memory(query)

        EDITING RULES:
        - If file context is present ([Fayl: path] block) → use it directly, do NOT call read_file again
        - Prefer minimal edits over full rewrites
        - Inspect (read_file / list_directory) before edit when the target location is unknown

        AFTER TOOL RESULT:
        - Inspect the result: did it solve the task?
        - If yes → write final answer
        - If no → call the next needed tool
        - If tool failed → try alternative approach, do not retry the exact same call

        CONTEXT AWARENESS:
        - The LAST user message in the conversation is the CURRENT request. Focus ONLY on that.
        - Previous tool calls from earlier turns are already done. Do NOT repeat them for a new unrelated request.
        - Each new user message is a completely fresh task. Treat it independently.
        - If the current message is about web, coding, or something unrelated to the previous topic, address it directly.
        """;

    // Fallback model zanjiri: birinchi limit tugasa keyingisi ishlatiladi
    private static readonly (string BaseUrl, string Model, string EnvKey)[] FallbackChain =
    [
        ("https://api.groq.com/openai", "meta-llama/llama-4-scout-17b-16e-instruct", "GROQ_API_KEY"),
        ("https://api.groq.com/openai", "llama-3.3-70b-versatile",                   "GROQ_API_KEY"),
        ("https://api.deepseek.com/v1", "deepseek-chat",                              "DEEPSEEK_API_KEY"),
    ];

    private readonly IConfiguration _config;

    // Context oynasi: LLM ga faqat oxirgi N ta xabar yuboriladi
    private const int MaxContextMessages = 20;

    public LocalLlmClient(IConfiguration config)
    {
        _config  = config;
        _baseUrl = config["LLM_URL"] ?? "https://api.groq.com/openai";
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        var apiKey = config["GROQ_API_KEY"];
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public string DefaultModel => "meta-llama/llama-4-scout-17b-16e-instruct";

    // Xabarlar ro'yxatini context oynasiga kesish (system xabar saqlanadi)
    private static List<LlmMessage> TrimContext(List<LlmMessage> messages)
    {
        if (messages.Count <= MaxContextMessages) return messages;
        var system = messages.Where(m => m.Role == "system").ToList();
        var rest   = messages.Where(m => m.Role != "system")
                             .TakeLast(MaxContextMessages - system.Count).ToList();
        return [.. system, .. rest];
    }

    // ── Non-streaming chat with optional tools (fallback bilan) ──
    public async Task<LlmResponse> ChatAsync(
        List<LlmMessage> messages,
        List<ToolDefinition>? tools = null,
        string? model = null,
        CancellationToken ct = default)
    {
        var trimmed = TrimContext(messages);
        Exception? lastEx = null;

        foreach (var (baseUrl, fallbackModel, envKey) in FallbackChain)
        {
            var key = _config[envKey];
            if (string.IsNullOrEmpty(key)) continue;

            var body    = BuildRequestNode(fallbackModel, trimmed, tools, stream: false);
            var reqJson = body.ToJsonString();
            var cnt     = new StringContent(reqJson, Encoding.UTF8, "application/json");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
            {
                Content = cnt
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
                return ParseResponse(json!);
            }

            var errBody = await resp.Content.ReadAsStringAsync(ct);
            // 429 = rate limit, 503 = overloaded → keyingisiga o't
            if ((int)resp.StatusCode == 429 || (int)resp.StatusCode == 503)
            {
                lastEx = new HttpRequestException($"[{fallbackModel}] limit: {errBody[..Math.Min(100,errBody.Length)]}");
                continue;
            }
            // Boshqa xato — to'xtat
            throw new HttpRequestException($"LLM {(int)resp.StatusCode}: {errBody[..Math.Min(300,errBody.Length)]}");
        }

        throw lastEx ?? new HttpRequestException("Barcha LLM providerlari ishlamayapti");
    }

    // ── Streaming tokens (SSE format) ──
    public async IAsyncEnumerable<string> StreamAsync(
        List<LlmMessage> messages,
        string? model = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var groqKey  = _config["GROQ_API_KEY"] ?? "";
        var bodyNode = BuildRequestNode(DefaultModel, TrimContext(messages), null, stream: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(bodyNode.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(groqKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", groqKey);

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
        var messages = new List<LlmMessage>
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

    public LlmMessage BuildSystemMessage(string contextBlock) =>
        new("system", $"{SystemPrompt}\n\n{contextBlock}");

    // ── Build full request body as JsonNode (avoids CamelCase/object serialization issues) ──
    private static JsonNode BuildRequestNode(string model, List<LlmMessage> messages,
        List<ToolDefinition>? tools, bool stream)
    {
        var msgArray = new JsonArray();
        foreach (var m in messages)
            msgArray.Add(SerializeMessageNode(m));

        var body = new JsonObject
        {
            ["model"]       = model,
            ["messages"]    = msgArray,
            ["stream"]      = stream,
            ["max_tokens"]  = 8192,
            ["temperature"] = 0.6
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
            body["tools"]       = toolArray;
            body["tool_choice"] = "auto"; // model doimo tool ishlatishi uchun
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

    private static JsonNode SerializeMessageNode(LlmMessage m)
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
            obj["content"]    = m.Content ?? string.Empty; // Assistant matni saqlanib qolishi kerak!
            obj["tool_calls"] = JsonNode.Parse(m.ToolCallsJson);
            return obj;
        }

        obj["content"] = m.Content;
        return obj;
    }

    // ── Parse OpenAI-compatible response ──
    private static LlmResponse ParseResponse(JsonNode json)
    {
        // OpenAI format: choices[0].message
        var msg     = json["choices"]?[0]?["message"];
        var content = msg?["content"]?.GetValue<string>() ?? string.Empty;
        var toolCalls = new List<LlmToolCall>();

        var rawCalls = msg?["tool_calls"]?.AsArray();
        if (rawCalls != null)
        {
            foreach (var tc in rawCalls)
            {
                var fn = tc?["function"];
                if (fn == null) continue;
                toolCalls.Add(new LlmToolCall(
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
                    toolCalls.Add(new LlmToolCall(name, args));
            }
            catch { }
        }

        // Text pattern fallback: FAQAT model "→ tool_name(...)" deb yozganda ishlatiladi.
        // Kod yoki tushuntirish matni bo'lsa (``` yoki ko'p qator) — ishlatilmaydi.
        if (toolCalls.Count == 0 && !string.IsNullOrWhiteSpace(content)
            && content.Contains('→')
            && !content.Contains("```"))
            toolCalls.AddRange(ParseTextPatternToolCalls(content));

        return new LlmResponse(content, toolCalls);
    }

    // ── Text-pattern tool call detector ──────────────────────────────────
    // qwen2.5:7b ba'zan tool_calls API o'rniga "→ run_command("cmd")" deb yozadi
    private static readonly HashSet<string> KnownTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_command","run_powershell","run_python","open_app","web_search","read_url",
        "read_file","write_file","list_directory","search_files","delete_file","rename_file",
        "get_system_info","get_time","get_env","take_screenshot","get_clipboard","set_clipboard",
        "list_windows","focus_window","close_window","set_volume","save_memory","recall_memory"
    };

    // Primary parameter per tool (for single-arg calls)
    private static readonly Dictionary<string, string> ToolPrimaryParam =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["run_command"]    = "command",
        ["run_powershell"] = "script",
        ["run_python"]     = "code",
        ["open_app"]       = "app_name",
        ["web_search"]     = "query",
        ["read_url"]       = "url",
        ["read_file"]      = "path",
        ["list_directory"] = "path",
        ["delete_file"]    = "path",
        ["search_files"]   = "pattern",
        ["get_system_info"]= "type",
        ["get_time"]       = "format",
        ["get_env"]        = "name",
        ["take_screenshot"]= "filename",
        ["get_clipboard"]  = "dummy",
        ["set_clipboard"]  = "text",
        ["list_windows"]   = "dummy",
        ["focus_window"]   = "title",
        ["close_window"]   = "title",
        ["set_volume"]     = "level",
        ["save_memory"]    = "key",
        ["recall_memory"]  = "query",
    };

    private static List<LlmToolCall> ParseTextPatternToolCalls(string content)
    {
        var calls = new List<LlmToolCall>();
        // Match: [→ ]tool_name("arg") or tool_name({"k":"v"})
        var rx = new System.Text.RegularExpressions.Regex(
            @"(?:→\s*|^|\n)([a-z_]+)\s*\((.{0,500}?)\)\s*(?:\n|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match m in rx.Matches(content))
        {
            var name    = m.Groups[1].Value.Trim();
            var rawArgs = m.Groups[2].Value.Trim();
            if (!KnownTools.Contains(name)) continue;

            string argsJson;
            if (rawArgs.StartsWith("{"))
            {
                argsJson = rawArgs;
            }
            else
            {
                // Single arg — strip quotes, map to primary param
                var argVal = rawArgs.Trim('"', '\'', ' ');
                var param  = ToolPrimaryParam.GetValueOrDefault(name, "value");
                argsJson   = $"{{\"{param}\": \"{argVal.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"}}";}

            try { System.Text.Json.JsonDocument.Parse(argsJson); } catch { continue; }
            calls.Add(new LlmToolCall(name, argsJson));
            break; // bitta tool call — bir vaqtda bir tool
        }
        return calls;
    }
}

// ---- Value types ----

public record LlmMessage(string Role, string Content,
    string? ToolCallId = null, string? ToolCallsJson = null);

public record LlmResponse(string Content, List<LlmToolCall> ToolCalls)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
}

public record LlmToolCall(string Name, string ArgsJson, string Id = "");

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
