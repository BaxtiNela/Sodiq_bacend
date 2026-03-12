using System.IO;
using System.Text.Json;

namespace WA.App.Services;

/// <summary>
/// Foydalanuvchi profilini lokal saqlaydi.
/// Birinchi ishga tushganda token yaratiladi — qayta ochilganda ham o'sha token ishlatiladi.
/// </summary>
public class UserProfileStore
{
    private readonly string _filePath;
    private UserLocalProfile? _cached;

    public UserProfileStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "user_profile.json");
    }

    public UserLocalProfile? Load()
    {
        if (_cached != null) return _cached;
        try
        {
            if (!File.Exists(_filePath)) return null;
            _cached = JsonSerializer.Deserialize<UserLocalProfile>(File.ReadAllText(_filePath));
            return _cached;
        }
        catch { return null; }
    }

    public void Save(UserLocalProfile profile)
    {
        _cached = profile;
        File.WriteAllText(_filePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool IsRegistered => Load() != null;
}

public record UserLocalProfile(
    string Token,
    string Name,
    string Company,
    string Language = "uz");
