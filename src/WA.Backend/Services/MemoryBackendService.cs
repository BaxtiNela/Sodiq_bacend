using Microsoft.EntityFrameworkCore;
using WA.Backend.Data;
using WA.Backend.Models;

namespace WA.Backend.Services;

public class MemoryBackendService(AppDbContext db)
{
    public async Task<MemoryEntry> SaveAsync(string key, string value, string category = "fact", string tags = "")
    {
        var existing = await db.Memories
            .FirstOrDefaultAsync(m => m.Key.ToLower() == key.ToLower());

        if (existing != null)
        {
            existing.Value = value;
            existing.Category = category;
            existing.Tags = tags;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.Importance = Math.Min(1f, existing.Importance + 0.05f);
        }
        else
        {
            existing = new MemoryEntry
            {
                Key = key,
                Value = value,
                Category = category,
                Tags = tags
            };
            db.Memories.Add(existing);
        }

        await db.SaveChangesAsync();
        return existing;
    }

    public async Task<List<MemoryEntry>> SearchAsync(string query)
    {
        var words = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var all = await db.Memories.OrderByDescending(m => m.Importance).ToListAsync();

        return all
            .Where(m => words.Any(w =>
                m.Key.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                m.Value.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                m.Tags.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(15)
            .ToList();
    }

    public async Task<List<MemoryEntry>> GetTopAsync(int n = 20)
    {
        return await db.Memories
            .OrderByDescending(m => m.Importance * 0.6f + m.AccessCount * 0.4f)
            .Take(n)
            .ToListAsync();
    }

    public async Task IncrementAccessAsync(int id)
    {
        var entry = await db.Memories.FindAsync(id);
        if (entry == null) return;
        entry.AccessCount++;
        entry.LastAccessedAt = DateTime.UtcNow;
        entry.Importance = Math.Min(1f, entry.Importance + 0.02f);
        await db.SaveChangesAsync();
    }

    public async Task<List<MemoryEntry>> GetAllAsync() =>
        await db.Memories.OrderByDescending(m => m.UpdatedAt).ToListAsync();

    public async Task DeleteAsync(int id)
    {
        var entry = await db.Memories.FindAsync(id);
        if (entry != null)
        {
            db.Memories.Remove(entry);
            await db.SaveChangesAsync();
        }
    }
}
