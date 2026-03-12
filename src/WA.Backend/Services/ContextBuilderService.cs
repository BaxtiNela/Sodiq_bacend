using Microsoft.EntityFrameworkCore;
using WA.Backend.Data;
using WA.Backend.Models;

namespace WA.Backend.Services;

public class ContextBuilderService(AppDbContext db, MemoryBackendService memory)
{
    /// <summary>
    /// Yangi xabar uchun to'liq kontekst blokini quradi:
    /// ega profili + tegishli xotiralar + odatlar + oxirgi 20 xabar
    /// </summary>
    public async Task<string> BuildAsync(string currentMessage, string sessionId, string? userToken = null)
    {
        var sb = new System.Text.StringBuilder();

        // 1. Ega profili (qisqa) — token bo'yicha yuklanadi
        var profile = userToken != null
            ? await db.OwnerProfiles.FirstOrDefaultAsync(p => p.UserToken == userToken)
              ?? await db.OwnerProfiles.FirstOrDefaultAsync()
            : await db.OwnerProfiles.FirstOrDefaultAsync();

        if (profile != null)
        {
            sb.Append($"[FOYDALANUVCHI: {profile.Name}");
            if (!string.IsNullOrEmpty(profile.Company))
                sb.Append($" | {profile.Company}");
            sb.AppendLine($" | til: {profile.Language}]");
        }

        // 2. Doimiy ko'rsatmalar va ish uslubi (har doim yuboriladi — token sarfi kam)
        var instructions = await db.Memories
            .Where(m => m.Category == "instruction" || m.Category == "work_style")
            .OrderByDescending(m => m.Importance)
            .Take(5)
            .ToListAsync();

        if (instructions.Count > 0)
        {
            sb.AppendLine("[KO'RSATMALAR]");
            foreach (var m in instructions)
                sb.AppendLine($"- {m.Value}");
        }

        // 3. Joriy xabarga tegishli xotiralar (semantic search)
        var relevant = await memory.SearchAsync(currentMessage);
        var relevantFiltered = relevant
            .Where(m => m.Category != "instruction" && m.Category != "work_style")
            .Take(6)
            .ToList();

        // Agar tegishli xotira yetarli bo'lmasa, muhim xotiralarni qo'shish
        if (relevantFiltered.Count < 3)
        {
            var top = await db.Memories
                .Where(m => m.Category != "instruction" && m.Category != "work_style")
                .OrderByDescending(m => m.Importance * 0.6f + m.AccessCount * 0.4f)
                .Take(5)
                .ToListAsync();
            relevantFiltered = relevantFiltered
                .Concat(top)
                .DistinctBy(m => m.Id)
                .Take(6)
                .ToList();
        }

        if (relevantFiltered.Count > 0)
        {
            sb.AppendLine("[XOTIRA]");
            foreach (var m in relevantFiltered)
            {
                var label = m.Category switch
                {
                    "project"    => "loyiha",
                    "fact"       => "fakt",
                    "habit"      => "odat",
                    "preference" => "afzallik",
                    "success"    => "✓",
                    "failure"    => "✗",
                    _            => m.Category
                };
                sb.AppendLine($"[{label}] {m.Key}: {m.Value}");
                _ = memory.IncrementAccessAsync(m.Id);
            }
        }

        // 4. Eng ko'p ishlatiladigan odatlar (qisqa)
        var habits = await db.Habits
            .OrderByDescending(h => h.Frequency)
            .Take(3)
            .ToListAsync();

        if (habits.Count > 0)
        {
            sb.Append("[ODATLAR] ");
            sb.AppendLine(string.Join(" | ", habits.Select(h => h.Pattern)));
        }

        // NOT: Suhbat tarixi bu yerda YUBORILMAYDI —
        // Hub messages array orqali LLM ga to'g'ridan-to'g'ri uzatiladi (dubliklashni oldini olish)

        return sb.ToString().Trim();
    }

    /// <summary>Suhbat sarlavhasini avtomatik aniqlash</summary>
    public static string InferTitle(string firstMessage)
    {
        if (firstMessage.Length <= 50) return firstMessage;
        return firstMessage[..47] + "...";
    }
}
