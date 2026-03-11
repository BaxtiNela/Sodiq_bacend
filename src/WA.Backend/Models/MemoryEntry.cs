namespace WA.Backend.Models;

public class MemoryEntry
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    /// <summary>fact | habit | preference | skill | event</summary>
    public string Category { get; set; } = "fact";

    /// <summary>0.0 – 1.0, avtomatik o'sib boradi</summary>
    public float Importance { get; set; } = 0.5f;

    public int AccessCount { get; set; } = 0;

    /// <summary>Vergul bilan ajratilgan teglar</summary>
    public string Tags { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}
