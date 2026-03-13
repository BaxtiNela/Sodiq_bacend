using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WA.Backend.Data;
using WA.Backend.Models;
using WA.Backend.Services;

namespace WA.Backend.Hubs;

/// <summary>
/// Real-time bidirectional hub:
///   WPF App ←──SignalR──→ AssistantHub ──HTTP──→ Ollama
/// Tool call'lar WPF ga yo'naltiriladi (Docker CMD bajara olmaydi)
/// </summary>
public class AssistantHub(
    AppDbContext db,
    LocalLlmClient ollama,
    ContextBuilderService ctxBuilder,
    MemoryBackendService memSvc,
    LearningService learner,
    ILogger<AssistantHub> logger) : Hub
{
    private const int MaxRounds = 15;

    // Kutilayotgan tool result'lar: connectionId -> (callId -> TaskCompletionSource)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>>>
        _pendingTools = new();

    // Bekor qilish tokenlari: connectionId -> CancellationTokenSource
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>
        _cancelTokens = new();

    // ═══════════════════════════════════════════════════════════════
    // CLIENT → SERVER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Yangi xabar yuborish</summary>
    public async Task SendMessage(string sessionId, string userMessage, string? model = null, string? userToken = null)
    {
        var connId = Context.ConnectionId;
        logger.LogInformation("[{Conn}] SendMessage: session={Session}", connId, sessionId);

        // Yangi so'rov uchun CancellationTokenSource yaratish
        var cts = new CancellationTokenSource();
        _cancelTokens[connId] = cts;
        var ct = cts.Token;

        try
        {
            // Sessiyani olish yoki yaratish
            var conv = await GetOrCreateConversationAsync(sessionId, userMessage);

            // Foydalanuvchi xabarini saqlash
            conv.Messages.Add(new Message { Role = "user", Content = userMessage });
            await db.SaveChangesAsync();

            await Clients.Caller.SendAsync("StatusUpdate", "Kontekst yuklanmoqda...");

            // Kontekst qurish (token orqali foydalanuvchi profili yuklanadi)
            var contextBlock = await ctxBuilder.BuildAsync(userMessage, sessionId, userToken);
            var systemMsg = ollama.BuildSystemMessage(contextBlock);

            // Barcha xabarlarni yig'ish (session tarixi)
            // tool xabarlarini o'tkazib yuboramiz — ularda tool_call_id kerak, eski DB da yo'q
            // Har bir xabarni 1500 belgiga cheklaymiz — token limitini oshirmaslik uchun
            var history = conv.Messages
                .OrderBy(m => m.CreatedAt)
                .Where(m => (m.Role == "user" || m.Role == "assistant")
                            && !string.IsNullOrEmpty(m.Content))
                .TakeLast(12)
                .Select(m => new LlmMessage(m.Role,
                    m.Content.Length > 1500 ? m.Content[..1500] + "…" : m.Content))
                .ToList();

            // System message boshida bo'lsin
            var messages = new List<LlmMessage> { systemMsg };
            messages.AddRange(history);

            var tools = GetAllTools();
            string finalResponse = string.Empty;

            // Loop detection: bir xil tool+args ni 3 marta qaytarsa to'xtatamiz
            string lastToolKey = string.Empty;
            int lastToolRepeat = 0;

            // ── Agent loop ──
            for (int round = 0; round < MaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                await Clients.Caller.SendAsync("StatusUpdate", round == 0 ? "O'ylayapman..." : $"Davom etmoqda... ({round})");

                var resp = await ollama.ChatAsync(messages, tools, model, ct);

                if (!resp.HasToolCalls)
                {
                    // Yakuniy javob — stream simulatsiyasi
                    finalResponse = resp.Content;
                    foreach (var chunk in ChunkString(finalResponse, 15))
                        await Clients.Caller.SendAsync("StreamToken", chunk);
                    break;
                }

                // ── Loop detection: bir xil tool+args 3 marta qaytarsa to'xtatish ──
                var firstTc = resp.ToolCalls[0];
                var currentKey = $"{firstTc.Name}|{firstTc.ArgsJson}";
                if (currentKey == lastToolKey)
                {
                    lastToolRepeat++;
                    if (lastToolRepeat >= 2)
                    {
                        finalResponse = $"Tool '{firstTc.Name}' bir necha marta qaytarildi. Natija olish mumkin emas.";
                        await Clients.Caller.SendAsync("StreamToken", finalResponse);
                        break;
                    }
                }
                else
                {
                    lastToolKey = currentKey;
                    lastToolRepeat = 0;
                }

                // Tool call'larni bajarish — assistant xabari tool_calls bilan
                var tcJson = System.Text.Json.JsonSerializer.Serialize(
                    resp.ToolCalls.Select(t => new {
                        id = t.Id, type = "function",
                        function = new { name = t.Name, arguments = t.ArgsJson }
                    }));
                messages.Add(new LlmMessage("assistant", resp.Content ?? string.Empty,
                    ToolCallsJson: tcJson));

                foreach (var tc in resp.ToolCalls)
                {
                    var callId = tc.Id;
                    if (string.IsNullOrEmpty(callId)) callId = Guid.NewGuid().ToString("N")[..8];
                    await Clients.Caller.SendAsync("StatusUpdate", $"Tool: {tc.Name}...");

                    // WPF ga tool bajarish so'rovi
                    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var connPending = _pendingTools.GetOrAdd(connId,
                        _ => new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>>());
                    connPending[callId] = tcs;

                    await Clients.Caller.SendAsync("ToolCallRequest", tc.Id, tc.Name, tc.ArgsJson);

                    // 60 soniya kutish
                    using var toolCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    toolCts.Token.Register(() => tcs.TrySetResult("[timeout]"));

                    var toolResult = await tcs.Task;

                    if (_pendingTools.TryGetValue(connId, out var cp))
                        cp.TryRemove(callId, out _);

                    logger.LogInformation("[{Conn}] Tool {Name} result: {Result}",
                        connId, tc.Name, toolResult[..Math.Min(100, toolResult.Length)]);

                    // Tool natijasini DB ga saqlash
                    conv.Messages.Add(new Message
                    {
                        Role = "tool",
                        Content = toolResult,
                        ToolName = tc.Name,
                        ToolCallId = tc.Id
                    });
                    await db.SaveChangesAsync();

                    // read_file dan keyin write_file ga yo'naltirish
                    var toolMsg = toolResult;
                    if (tc.Name == "read_file" && !toolResult.StartsWith("[Xato]"))
                        toolMsg = toolResult + "\n\n[SYSTEM: Fayl o'qildi. Endi FAQAT write_file() chaqir va to'liq yangilangan kodni yoz. read_file() qayta chaqirma.]";

                    // Tool natijasini xabarlarga qo'shish (tool_call_id majburiy)
                    messages.Add(new LlmMessage("tool", toolMsg, ToolCallId: tc.Id));

                    await Clients.Caller.SendAsync("ToolResult", tc.Name, toolResult[..Math.Min(500, toolResult.Length)]);
                }
            }

            if (string.IsNullOrEmpty(finalResponse))
            {
                finalResponse = "Maksimal qadam soniga yetildi.";
                await Clients.Caller.SendAsync("StreamToken", finalResponse);
            }

            // Yakuniy javobni DB ga saqlash
            conv.Messages.Add(new Message { Role = "assistant", Content = finalResponse });
            await db.SaveChangesAsync();

            await Clients.Caller.SendAsync("FinalResponse", finalResponse, sessionId);

            // Background: o'rganish + eski xabarlarni tozalash (faqat so'nggi 6 ta saqlanadi)
            _ = Task.Run(async () => {
                await learner.ExtractAndSaveAsync(userMessage, finalResponse);
                await learner.PruneOldMessagesAsync(conv.Id);
            });
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[{Conn}] SendMessage bekor qilindi", connId);
            await Clients.Caller.SendAsync("FinalResponse", "", sessionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SendMessage error");
            await Clients.Caller.SendAsync("Error", $"Xato: {ex.Message}");
        }
        finally
        {
            _cancelTokens.TryRemove(connId, out _);
            cts.Dispose();
        }
    }

    /// <summary>Session tarixini tozalash — xotira saqlanadi, faqat xabarlar o'chiriladi</summary>
    public async Task ClearHistory(string sessionId)
    {
        var conv = await db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.SessionId == sessionId);
        if (conv == null) return;
        db.Messages.RemoveRange(conv.Messages);
        await db.SaveChangesAsync();
        logger.LogInformation("ClearHistory: session={Session}", sessionId);
        await Clients.Caller.SendAsync("HistoryCleared", sessionId);
    }

    /// <summary>WPF tool natijasini qaytaradi</summary>
    public Task SendToolResult(string sessionId, string callId, string toolName, string result)
    {
        var connId = Context.ConnectionId;
        if (_pendingTools.TryGetValue(connId, out var pending) &&
            pending.TryGetValue(callId, out var tcs))
        {
            tcs.TrySetResult(result);
        }
        return Task.CompletedTask;
    }

    /// <summary>Pulli AI javobidan o'rganish — local modellar uchun xotiraga saqlash</summary>
    public async Task LearnFromExternal(string userMsg, string aiResponse, string provider)
    {
        logger.LogInformation("LearnFromExternal: provider={Provider}, q={Q}",
            provider, userMsg[..Math.Min(60, userMsg.Length)]);
        await Task.Run(() =>
            learner.ExtractAndSaveAsync(
                $"[{provider}] {userMsg}",
                aiResponse));
    }

    /// <summary>So'rovni bekor qilish</summary>
    public Task CancelRequest(string sessionId)
    {
        var connId = Context.ConnectionId;

        // Asosiy agent loop ni to'xtatish
        if (_cancelTokens.TryGetValue(connId, out var cts))
            cts.Cancel();

        // Kutilayotgan tool call'larni ham to'xtatish
        if (_pendingTools.TryGetValue(connId, out var pending))
        {
            foreach (var tcs in pending.Values)
                tcs.TrySetResult("[cancelled]");
            pending.Clear();
        }

        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var connId = Context.ConnectionId;
        if (_cancelTokens.TryRemove(connId, out var cts))
            cts.Cancel();
        if (_pendingTools.TryRemove(connId, out var pending))
            foreach (var tcs in pending.Values)
                tcs.TrySetResult("[disconnected]");
        return base.OnDisconnectedAsync(exception);
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════

    private async Task<Conversation> GetOrCreateConversationAsync(string sessionId, string firstMsg)
    {
        var conv = await db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.SessionId == sessionId);

        if (conv == null)
        {
            conv = new Conversation
            {
                SessionId = sessionId,
                Title = ContextBuilderService.InferTitle(firstMsg)
            };
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
        }

        return conv;
    }

    private static IEnumerable<string> ChunkString(string text, int size)
    {
        for (int i = 0; i < text.Length; i += size)
            yield return text[i..Math.Min(i + size, text.Length)];
    }

    /// <summary>WPF ToolExecutor bilan mos keluvchi tool ta'riflari</summary>
    private static List<ToolDefinition> GetAllTools() =>
    [
        // ── Tizim buyruqlari ──
        Tool("run_command", "Windows CMD buyrug'ini bajarish va natijani olish",
            Props(P("command", "CMD buyrug'i")),
            ["command"]),

        Tool("run_powershell", "PowerShell skriptini bajarish",
            Props(P("script", "PowerShell kodi")),
            ["script"]),

        // ── Fayl operatsiyalari ──
        Tool("read_file", "Fayl mazmunini o'qish",
            Props(P("path", "Fayl yo'li")),
            ["path"]),

        Tool("write_file", "Faylga yozish yoki yangi fayl yaratish",
            Props(P("path", "Fayl yo'li"), P("content", "Yoziladigan mazmun")),
            ["path", "content"]),

        Tool("list_directory", "Papka tarkibini ko'rish",
            Props(P("path", "Papka yo'li (bo'sh = home)")),
            ["path"]),

        Tool("search_files", "Fayllarda qidirish (*.py, *.txt kabi)",
            Props(P("pattern", "Pattern (masalan: *.py)"), P("directory", "Qidiruv papkasi")),
            ["pattern", "directory"]),

        Tool("delete_file", "Faylni o'chirish",
            Props(P("path", "O'chiriladigan fayl yo'li")),
            ["path"]),

        Tool("rename_file", "Faylni qayta nomlash yoki ko'chirish",
            Props(P("old_path", "Eski yo'l"), P("new_path", "Yangi yo'l")),
            ["old_path", "new_path"]),

        // ── Tizim ma'lumotlari ──
        Tool("get_system_info", "CPU, RAM, disk, OS ma'lumotlari",
            Props(P("type", "all|cpu|memory|disk|os")),
            ["type"]),

        Tool("get_time", "Hozirgi vaqt, sana va timezone",
            Props(P("format", "short|full|iso (ixtiyoriy)")),
            []),

        Tool("get_env", "Environment o'zgaruvchisini o'qish",
            Props(P("name", "O'zgaruvchi nomi (PATH, TEMP, USERNAME, ...)")),
            ["name"]),

        // ── Desktop boshqaruv ──
        Tool("open_app", "Dastur ochish",
            Props(P("name", "Dastur nomi (chrome, notepad, code, ...)")),
            ["name"]),

        Tool("take_screenshot", "Ekran rasmini olish va saqlash",
            Props(P("filename", "Fayl nomi ixtiyoriy (.png)")),
            []),

        Tool("get_clipboard", "Clipboard (bufer) mazmunini o'qish",
            Props(P("dummy", "ixtiyoriy")),
            []),

        Tool("set_clipboard", "Clipboardga matn yozish",
            Props(P("text", "Clipboardga yoziladigan matn")),
            ["text"]),

        Tool("list_windows", "Hozir ochiq oynalar ro'yxati",
            Props(P("dummy", "ixtiyoriy")),
            []),

        Tool("focus_window", "Oynani oldinga chiqarish",
            Props(P("title", "Oyna sarlavhasidan qism (Notepad, Chrome)")),
            ["title"]),

        Tool("close_window", "Oynani yopish",
            Props(P("title", "Oyna sarlavhasidan qism")),
            ["title"]),

        Tool("set_volume", "Ovoz balandligini o'rnatish (0-100)",
            Props(P("level", "0-100 son")),
            ["level"]),

        // ── Internet ──
        Tool("web_search", "DuckDuckGo orqali internetdan qidirish. Sarlavha + URL + snippet qaytaradi",
            Props(P("query", "Qidiruv so'zi"), P("num_results", "Natijalar soni (default 5)")),
            ["query"]),

        Tool("read_url", "URL veb-sahifasini o'qib mazmunini chiqarish",
            Props(P("url", "To'liq URL (https://...)"), P("max_chars", "Max belgilar (default 5000)")),
            ["url"]),

        // ── Xotira ──
        Tool("save_memory", "Muhim ma'lumotni doimiy xotiraga saqlash",
            Props(P("key", "Kalit"), P("value", "Qiymat"), P("category", "fact|project|instruction|habit|preference")),
            ["key", "value"]),

        Tool("recall_memory", "Xotiradan ma'lumot qidirish",
            Props(P("query", "Qidiruv so'zi")),
            ["query"]),
    ];

    private static ToolDefinition Tool(string name, string desc,
        Dictionary<string, ToolProperty> props, List<string> required) =>
        new("function", new ToolFunction(name, desc, new ToolParameters("object", props, required)));

    private static Dictionary<string, ToolProperty> Props(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => new ToolProperty("string", p.Item2));

    private static (string, string) P(string name, string desc) => (name, desc);
}
