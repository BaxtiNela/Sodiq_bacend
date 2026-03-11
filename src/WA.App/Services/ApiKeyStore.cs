using System.IO;
using System.Text.Json;

namespace WA.App.Services;

/// <summary>API kalitlarini AppData da JSON fayl sifatida saqlaydi</summary>
public class ApiKeyStore
{
    private readonly string _filePath;
    private Dictionary<string, string> _keys;

    public ApiKeyStore(string dataDir)
    {
        _filePath = Path.Combine(dataDir, "api_keys.json");
        _keys = Load();
    }

    public string? Get(string provider) =>
        _keys.TryGetValue(provider.ToLower(), out var k) ? k : null;

    public void Set(string provider, string key)
    {
        _keys[provider.ToLower()] = key;
        Save();
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return [];
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch { return []; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_keys,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
