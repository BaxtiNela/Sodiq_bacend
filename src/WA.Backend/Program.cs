using Microsoft.EntityFrameworkCore;
using WA.Backend.Data;
using WA.Backend.Hubs;
using WA.Backend.Models;
using WA.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Konfiguratsiya ──
var dbPath = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=/data/assistant.db";

// ── Servislar ──
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlite(dbPath));

builder.Services.AddSignalR(opt =>
{
    opt.MaximumReceiveMessageSize = 1024 * 1024; // 1 MB
    opt.ClientTimeoutInterval = TimeSpan.FromMinutes(5);
    opt.KeepAliveInterval = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<OllamaBackendClient>();
builder.Services.AddScoped<MemoryBackendService>();
builder.Services.AddScoped<ContextBuilderService>();
builder.Services.AddScoped<LearningService>();

builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddLogging();

var app = builder.Build();

// ── DB yaratish ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Default owner profile
    if (!db.OwnerProfiles.Any())
    {
        db.OwnerProfiles.Add(new OwnerProfile());
        db.SaveChanges();
    }
}

app.UseCors();

// ── SignalR ──
app.MapHub<AssistantHub>("/hub/assistant");

// ── REST API ──

// Suhbatlar
app.MapGet("/api/sessions", async (AppDbContext db) =>
{
    var sessions = await db.Conversations
        .Select(c => new
        {
            c.SessionId,
            c.Title,
            c.StartedAt,
            MessageCount = c.Messages.Count
        })
        .OrderByDescending(c => c.StartedAt)
        .ToListAsync();
    return Results.Ok(sessions);
});

app.MapGet("/api/history/{sessionId}", async (string sessionId, AppDbContext db) =>
{
    var conv = await db.Conversations
        .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
        .FirstOrDefaultAsync(c => c.SessionId == sessionId);
    return conv is null ? Results.NotFound() : Results.Ok(conv);
});

app.MapDelete("/api/sessions/{sessionId}", async (string sessionId, AppDbContext db) =>
{
    var conv = await db.Conversations.FirstOrDefaultAsync(c => c.SessionId == sessionId);
    if (conv != null) { db.Conversations.Remove(conv); await db.SaveChangesAsync(); }
    return Results.Ok();
});

// Xotira
app.MapGet("/api/memory", async (MemoryBackendService mem) =>
    Results.Ok(await mem.GetAllAsync()));

app.MapPost("/api/memory", async (MemorySaveRequest req, MemoryBackendService mem) =>
{
    var entry = await mem.SaveAsync(req.Key, req.Value, req.Category, req.Tags);
    return Results.Ok(entry);
});

app.MapDelete("/api/memory/{id:int}", async (int id, MemoryBackendService mem) =>
{
    await mem.DeleteAsync(id);
    return Results.Ok();
});

// Ega profili
app.MapGet("/api/profile", async (AppDbContext db) =>
    Results.Ok(await db.OwnerProfiles.FirstOrDefaultAsync()));

app.MapPut("/api/profile", async (ProfileUpdateRequest req, AppDbContext db) =>
{
    var profile = await db.OwnerProfiles.FirstOrDefaultAsync()
        ?? new OwnerProfile();

    if (req.Name != null) profile.Name = req.Name;
    if (req.Language != null) profile.Language = req.Language;
    if (req.Timezone != null) profile.Timezone = req.Timezone;
    if (req.WorkingHours != null) profile.WorkingHours = req.WorkingHours;
    if (req.Notes != null) profile.Notes = req.Notes;
    profile.LastSeen = DateTime.UtcNow;

    if (profile.Id == 0) db.OwnerProfiles.Add(profile);
    await db.SaveChangesAsync();
    return Results.Ok(profile);
});

// Odatlar
app.MapGet("/api/habits", async (AppDbContext db) =>
    Results.Ok(await db.Habits.OrderByDescending(h => h.Frequency).ToListAsync()));

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.Run();
