namespace WA.Backend.Models;

public class Conversation
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = "Yangi suhbat";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public List<Message> Messages { get; set; } = [];
}

public class Message
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public string Role { get; set; } = string.Empty; // user, assistant, tool, system
    public string Content { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Conversation Conversation { get; set; } = null!;
}
