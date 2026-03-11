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
    OllamaBackendClient ollama,
    ContextBuilderService ctxBuilder,
    MemoryBackendService memSvc,
    LearningService learner,
    ILogger<AssistantHub> logger) : Hub
{
    private const int MaxRounds = 15;

    // Kutilayotgan tool result'lar: connectionId -> (callId -> TaskCompletionSource)
    private static readonly Dictionary<string, Dictionary<string, TaskCompletionSource<string>>>
        _pendingTools = [];

    // ═══════════════════════════════════════════════════════════════
    // CLIENT → SERVER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Yangi xabar yuborish</summary>
    public async Task SendMessage(string sessionId, string userMessage, string? model = null)
    {
        var connId = Context.ConnectionId;
        logger.LogInformation("[{Conn}] SendMessage: session={Session}", connId, sessionId);

        try
        {
            // Sessiyani olish yoki yaratish
            var conv = await GetOrCreateConversationAsync(sessionId, userMessage);

            // Foydalanuvchi xabarini saqlash
            conv.Messages.Add(new Message { Role = "user", Content = userMessage });
            await db.SaveChangesAsync();

            await Clients.Caller.SendAsync("StatusUpdate", "Kontekst yuklanmoqda...");

            // Kontekst qurish
            var contextBlock = await ctxBuilder.BuildAsync(userMessage, sessionId);
            var systemMsg = ollama.BuildSystemMessage(contextBlock);

            // Barcha xabarlarni yig'ish (session tarixi)
            var history = conv.Messages
                .OrderBy(m => m.CreatedAt)
                .TakeLast(30)
                .Select(m => new OllamaMessage(m.Role, m.Content))
                .ToList();

            // System message boshida bo'lsin
            var messages = new List<OllamaMessage> { systemMsg };
            messages.AddRange(history);

            var tools = GetAllTools();
            string finalResponse = string.Empty;

            // ── Agent loop ──
            for (int round = 0; round < MaxRounds; round++)
            {
                await Clients.Caller.SendAsync("StatusUpdate", round == 0 ? "O'ylayapman..." : $"Davom etmoqda... ({round})");

                var resp = await ollama.ChatAsync(messages, tools, model);

                if (!resp.HasToolCalls)
                {
                    // Yakuniy javob — stream simulatsiyasi
                    finalResponse = resp.Content;
                    foreach (var chunk in ChunkString(finalResponse, 15))
                        await Clients.Caller.SendAsync("StreamToken", chunk);
                    break;
                }

                // Tool call'larni bajarish
                messages.Add(new OllamaMessage("assistant", resp.Content));

                foreach (var tc in resp.ToolCalls)
                {
                    var callId = Guid.NewGuid().ToString("N")[..8];
                    await Clients.Caller.SendAsync("StatusUpdate", $"Tool: {tc.Name}...");

                    // WPF ga tool bajarish so'rovi
                    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_pendingTools)
                    {
                        if (!_pendingTools.ContainsKey(connId))
                            _pendingTools[connId] = [];
                        _pendingTools[connId][callId] = tcs;
                    }

                    await Clients.Caller.SendAsync("ToolCallRequest", callId, tc.Name, tc.ArgsJson);

                    // 60 soniya kutish
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    cts.Token.Register(() => tcs.TrySetResult("[timeout]"));

                    var toolResult = await tcs.Task;

                    lock (_pendingTools)
                        _pendingTools[connId].Remove(callId);

                    logger.LogInformation("[{Conn}] Tool {Name} result: {Result}",
                        connId, tc.Name, toolResult[..Math.Min(100, toolResult.Length)]);

                    // Tool natijasini DB ga saqlash
                    conv.Messages.Add(new Message
                    {
                        Role = "tool",
                        Content = toolResult,
                        ToolName = tc.Name,
                        ToolCallId = callId
                    });
                    await db.SaveChangesAsync();

                    // Tool natijasini xabarlarga qo'shish
                    messages.Add(new OllamaMessage("tool", $"[{tc.Name}]: {toolResult}"));

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

            // Background: o'rganish
            _ = Task.Run(() => learner.ExtractAndSaveAsync(userMessage, finalResponse));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SendMessage error");
            await Clients.Caller.SendAsync("Error", $"Xato: {ex.Message}");
        }
    }

    /// <summary>WPF tool natijasini qaytaradi</summary>
    public Task SendToolResult(string sessionId, string callId, string toolName, string result)
    {
        var connId = Context.ConnectionId;
        lock (_pendingTools)
        {
            if (_pendingTools.TryGetValue(connId, out var pending) &&
                pending.TryGetValue(callId, out var tcs))
            {
                tcs.TrySetResult(result);
            }
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
        lock (_pendingTools)
        {
            if (_pendingTools.TryGetValue(connId, out var pending))
            {
                foreach (var tcs in pending.Values)
                    tcs.TrySetResult("[cancelled]");
                pending.Clear();
            }
        }
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        lock (_pendingTools)
            _pendingTools.Remove(Context.ConnectionId);
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
        Tool("run_command", "Windows CMD buyrug'ini bajarish",
            Props(P("command", "CMD buyrug'i"), P("working_dir", "Ishchi papka (ixtiyoriy)")),
            ["command"]),

        Tool("run_powershell", "PowerShell skriptini bajarish",
            Props(P("script", "PowerShell kodi")),
            ["script"]),

        Tool("read_file", "Fayl mazmunini o'qish",
            Props(P("path", "Fayl yo'li")),
            ["path"]),

        Tool("write_file", "Faylga yozish",
            Props(P("path", "Fayl yo'li"), P("content", "Yoziladigan mazmun")),
            ["path", "content"]),

        Tool("list_directory", "Papka tarkibini ko'rish",
            Props(P("path", "Papka yo'li")),
            ["path"]),

        Tool("search_files", "Fayllarda qidirish",
            Props(P("pattern", "Qidiruv namunasi"), P("directory", "Qidiruv papkasi")),
            ["pattern"]),

        Tool("get_system_info", "Tizim ma'lumotlarini olish",
            Props(P("type", "Tur: cpu|memory|disk|os|all")),
            ["type"]),

        Tool("open_app", "Dastur ochish",
            Props(P("name", "Dastur nomi yoki yo'li")),
            ["name"]),

        Tool("save_memory", "Xotiraga saqlash",
            Props(P("key", "Kalit"), P("value", "Qiymat"), P("category", "Tur: fact|habit|preference")),
            ["key", "value"]),

        Tool("recall_memory", "Xotiradan eslab olish",
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
