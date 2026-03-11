namespace WA.Backend.Models;

// REST API DTOs
public record MemorySaveRequest(string Key, string Value, string Category = "fact", string Tags = "");
public record ProfileUpdateRequest(string? Name, string? Language, string? Timezone, string? WorkingHours, string? Notes);

// SignalR message DTOs (JSON serializable)
public record ToolCallMsg(string CallId, string ToolName, string ArgsJson);
public record ToolResultMsg(string SessionId, string CallId, string ToolName, string Result);

// Extracted fact from LearningService
public record ExtractedFact(string Key, string Value, string Category);
public record ExtractionResult(List<ExtractedFact> Facts, List<string> Habits);
