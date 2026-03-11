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
    public async Task<string> BuildAsync(string currentMessage, string sessionId)
    {
        var sb = new System.Text.StringBuilder();

        // 1. Ega profili
        var profile = await db.OwnerProfiles.FirstOrDefaultAsync();
        if (profile != null)
        {
            sb.AppendLine("=== EGA PROFILI ===");
            sb.AppendLine($"Ism: {profile.Name}");
            sb.AppendLine($"Til: {profile.Language}");
            sb.AppendLine($"Vaqt mintaqasi: {profile.Timezone}");
            sb.AppendLine($"Ish soatlari: {profile.WorkingHours}");
            if (!string.IsNullOrEmpty(profile.Notes))
                sb.AppendLine($"Qo'shimcha: {profile.Notes}");
            sb.AppendLine();
        }

        // 2. Tegishli xotiralar (joriy xabarga mos keluvchilar)
        var relevant = await memory.SearchAsync(currentMessage);
        var top = await memory.GetTopAsync(10);

        // Ikkalasini birlashtirish, takrorlanmaslik uchun
        var combined = relevant.Concat(top)
            .DistinctBy(m => m.Id)
            .OrderByDescending(m => m.Importance)
            .Take(15)
            .ToList();

        if (combined.Count > 0)
        {
            sb.AppendLine("=== XOTIRA (O'RGANILGAN MA'LUMOTLAR) ===");
            foreach (var m in combined)
            {
                sb.AppendLine($"[{m.Category}] {m.Key}: {m.Value}");
                // Ko'rib chiqilgan xotirani muhimroq qil
                _ = memory.IncrementAccessAsync(m.Id);
            }
            sb.AppendLine();
        }

        // 3. Odatlar
        var habits = await db.Habits
            .OrderByDescending(h => h.Frequency)
            .Take(5)
            .ToListAsync();

        if (habits.Count > 0)
        {
            sb.AppendLine("=== EGA ODATLARI ===");
            foreach (var h in habits)
                sb.AppendLine($"- {h.Pattern} (x{h.Frequency})");
            sb.AppendLine();
        }

        // 4. Joriy sessiya tarixi (oxirgi 20 xabar)
        var conv = await db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.SessionId == sessionId);

        if (conv?.Messages.Count > 0)
        {
            var recent = conv.Messages
                .OrderBy(m => m.CreatedAt)
                .TakeLast(20)
                .ToList();

            sb.AppendLine("=== JORIY SUHBAT TARIXI ===");
            foreach (var msg in recent)
            {
                var prefix = msg.Role switch
                {
                    "user" => "Foydalanuvchi",
                    "assistant" => "Assistent",
                    "tool" => $"Tool ({msg.ToolName})",
                    _ => msg.Role
                };
                var content = msg.Content.Length > 300
                    ? msg.Content[..300] + "..."
                    : msg.Content;
                sb.AppendLine($"{prefix}: {content}");
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Suhbat sarlavhasini avtomatik aniqlash</summary>
    public static string InferTitle(string firstMessage)
    {
        if (firstMessage.Length <= 50) return firstMessage;
        return firstMessage[..47] + "...";
    }
}
