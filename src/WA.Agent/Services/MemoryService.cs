using System.Text.Json;

namespace WA.Agent.Services;

/// <summary>AI xotirasi - sessiyalar orasida saqlanadi</summary>
public class MemoryService
{
    private readonly string _filePath;
    private List<MemoryEntry> _entries = [];

    public MemoryService(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "ai_memory.json");
        Load();
    }

    public string Save(string key, string value, string category = "fact")
    {
        _entries.RemoveAll(e => e.Key == key);
        _entries.Add(new MemoryEntry(key, value, category, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (_entries.Count > 500) _entries = _entries.TakeLast(500).ToList();
        Persist();
        return $"✅ Xotirada saqlandi: [{category}] {key}";
    }

    public string Recall(string query)
    {
        if (_entries.Count == 0) return "Xotira bo'sh.";
        var q = query.ToLowerInvariant();
        var results = _entries
            .Where(e => e.Key.ToLower().Contains(q) || e.Value.ToLower().Contains(q))
            .TakeLast(10)
            .Select(e => $"[{e.Category}] {e.Key}: {e.Value}");
        return results.Any() ? string.Join("\n", results) : $"'{query}' bo'yicha hech narsa topilmadi.";
    }

    public string GetRecentContext(int count = 20)
    {
        if (_entries.Count == 0) return "";
        var recent = _entries.TakeLast(count)
            .Select(e => $"• [{e.Category}] {e.Key}: {e.Value}");
        return "## Xotiradagi ma'lumotlar:\n" + string.Join("\n", recent);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
                _entries = JsonSerializer.Deserialize<List<MemoryEntry>>(
                    File.ReadAllText(_filePath)) ?? [];
        }
        catch { _entries = []; }
    }

    private void Persist()
    {
        try { File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    private record MemoryEntry(string Key, string Value, string Category, long Timestamp);
}
