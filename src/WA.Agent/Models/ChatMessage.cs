namespace WA.Agent.Models;

public record ChatMessage(string Role, string Content);

public record ToolCall(string Id, string Name, Dictionary<string, object?> Arguments);

public record AgentResponse(string Content, List<ToolCall>? ToolCalls = null);

public record ToolResult(string ToolCallId, string Result);

public class ConversationHistory
{
    private readonly List<ChatMessage> _messages = [];
    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void Add(ChatMessage msg) => _messages.Add(msg);
    public void AddUser(string text) => _messages.Add(new ChatMessage("user", text));
    public void AddAssistant(string text) => _messages.Add(new ChatMessage("assistant", text));
    public void AddTool(string result) => _messages.Add(new ChatMessage("tool", result));
    public void Clear() => _messages.Clear();

    public void TrimToLast(int n)
    {
        if (_messages.Count > n)
            _messages.RemoveRange(0, _messages.Count - n);
    }
}
