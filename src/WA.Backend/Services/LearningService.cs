using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WA.Backend.Data;
using WA.Backend.Models;

namespace WA.Backend.Services;

/// <summary>
/// Har suhbat turnidan keyin AI yordamida faktlar, odatlar va ega profilini yangilaydi.
/// Asynxron, background'da ishlaydi — asosiy jarayonni bloklamas.
/// </summary>
public class LearningService(
    AppDbContext db,
    OllamaBackendClient ollama,
    MemoryBackendService memory,
    ILogger<LearningService> logger)
{
    public async Task ExtractAndSaveAsync(string userMsg, string assistantMsg, CancellationToken ct = default)
    {
        try
        {
            var raw = await ollama.ExtractFactsAsync(userMsg, assistantMsg, ct);
            if (string.IsNullOrWhiteSpace(raw)) return;

            // JSON blokini ajratib olish
            var jsonStart = raw.IndexOf('{');
            var jsonEnd = raw.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart) return;

            var json = raw[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Faktlarni saqlash
            if (root.TryGetProperty("facts", out var factsEl))
            {
                foreach (var fact in factsEl.EnumerateArray())
                {
                    var key = fact.GetStringSafe("key");
                    var value = fact.GetStringSafe("value");
                    var category = fact.GetStringSafe("category") ?? "fact";

                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
                    if (key.Length > 100 || value.Length > 500) continue;

                    await memory.SaveAsync(key, value, category);

                    // Ega profilini yangilash
                    await TryUpdateProfileAsync(key.ToLower(), value);
                }
            }

            // Odatlarni saqlash
            if (root.TryGetProperty("habits", out var habitsEl))
            {
                foreach (var habitEl in habitsEl.EnumerateArray())
                {
                    var pattern = habitEl.ValueKind == JsonValueKind.String
                        ? habitEl.GetString()
                        : habitEl.GetStringSafe("pattern");

                    if (string.IsNullOrWhiteSpace(pattern)) continue;

                    var existing = await db.Habits
                        .FirstOrDefaultAsync(h => h.Pattern == pattern, ct);

                    if (existing != null)
                    {
                        existing.Frequency++;
                        existing.LastSeen = DateTime.UtcNow;
                    }
                    else
                    {
                        db.Habits.Add(new Habit
                        {
                            Pattern = pattern,
                            TimeOfDay = InferTimeOfDay()
                        });
                    }
                }
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Learning extraction failed: {Msg}", ex.Message);
        }
    }

    private async Task TryUpdateProfileAsync(string key, string value)
    {
        var profile = await db.OwnerProfiles.FirstOrDefaultAsync();
        if (profile == null)
        {
            profile = new OwnerProfile();
            db.OwnerProfiles.Add(profile);
        }

        bool changed = false;

        if (key is "ism" or "name" or "ismi" && profile.Name == "Egasi")
        {
            profile.Name = value;
            changed = true;
        }
        else if (key is "til" or "language" or "tili")
        {
            profile.Language = value.ToLower() switch
            {
                "o'zbek" or "uzbek" or "uz" => "uz",
                "rus" or "russian" or "ru" => "ru",
                "ingliz" or "english" or "en" => "en",
                _ => profile.Language
            };
            changed = true;
        }
        else if (key is "timezone" or "vaqt mintaqasi")
        {
            profile.Timezone = value;
            changed = true;
        }

        if (changed)
        {
            profile.LastSeen = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    private static string InferTimeOfDay()
    {
        var hour = DateTime.Now.Hour;
        return hour switch
        {
            >= 5 and < 12 => "morning",
            >= 12 and < 17 => "afternoon",
            >= 17 and < 22 => "evening",
            _ => "night"
        };
    }
}

// Helper extension
internal static class JsonElementExtensions
{
    public static string? GetStringSafe(this JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var val) ? val.GetString() : null;
    }
}
