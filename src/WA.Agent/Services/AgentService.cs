using WA.Agent.Models;

namespace WA.Agent.Services;

/// <summary>
/// Asosiy agent orkestrator.
/// Tool calling loop: AI → tool → natija → AI → ... → yakuniy javob
/// </summary>
public class AgentService
{
    private readonly OllamaClient _ollama;
    private readonly ToolExecutor _executor;
    private readonly MemoryService _memory;
    private readonly ConversationHistory _history = new();

    public event Action<string>? OnStatusUpdate;
    public event Action<string, string>? OnToolCall;    // (tool_name, args_summary)
    public event Action<string>? OnToolResult;          // result preview

    private const int MaxRounds = 15;

    public AgentService(OllamaClient ollama, ToolExecutor executor, MemoryService memory)
    {
        _ollama = ollama;
        _executor = executor;
        _memory = memory;
    }

    public string CurrentModel => _ollama.Model;
    public void SetModel(string model) => _ollama.Model = model;
    public void ClearHistory() => _history.Clear();

    /// <summary>Foydalanuvchi xabarini qayta ishlash (tool loop bilan)</summary>
    public async Task<string> ProcessAsync(string userMessage, CancellationToken ct = default)
    {
        // Xotiradan kontekst qo'shish
        var memCtx = _memory.GetRecentContext();
        var fullMessage = string.IsNullOrEmpty(memCtx)
            ? userMessage
            : $"{memCtx}\n\n---\n{userMessage}";

        _history.AddUser(fullMessage);

        var tools = ToolExecutor.GetAllTools();
        var lastContent = "";

        for (int round = 0; round < MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            OnStatusUpdate?.Invoke(round == 0 ? "Fikrlanmoqda..." : $"Round {round + 1}...");

            var response = await _ollama.ChatAsync(_history, tools, ct);
            lastContent = response.Content;

            // Tool calls yo'q → bu yakuniy javob
            if (response.ToolCalls == null || response.ToolCalls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(lastContent))
                    _history.AddAssistant(lastContent);
                OnStatusUpdate?.Invoke("Tayyor");
                return lastContent;
            }

            // Tool calls bor → bajarish
            if (!string.IsNullOrWhiteSpace(lastContent))
                _history.AddAssistant(lastContent);

            foreach (var tc in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();

                var argsSummary = tc.Arguments.Count > 0
                    ? string.Join(", ", tc.Arguments.Select(kv => {
                        var val = kv.Value?.ToString() ?? "";
                        return $"{kv.Key}={(val.Length > 50 ? val[..50] + "..." : val)}";
                    }))
                    : "";

                OnStatusUpdate?.Invoke($"⚙ {tc.Name}({argsSummary})");
                OnToolCall?.Invoke(tc.Name, argsSummary);

                var result = await _executor.ExecuteAsync(tc);

                OnToolResult?.Invoke(result.Length > 200 ? result[..200] + "..." : result);
                _history.AddTool($"[{tc.Name}] {result}");
            }
        }

        OnStatusUpdate?.Invoke("Tayyor");
        return string.IsNullOrWhiteSpace(lastContent)
            ? "Maksimum qayta urinish. Iltimos, boshqacha so'rang."
            : lastContent;
    }

    /// <summary>Streaming chat (tool calling yo'q, faqat matn)</summary>
    public IAsyncEnumerable<string> StreamAsync(string userMessage, CancellationToken ct = default)
    {
        _history.AddUser(userMessage);
        return _ollama.ChatStreamAsync(_history, ct: ct);
    }
}
