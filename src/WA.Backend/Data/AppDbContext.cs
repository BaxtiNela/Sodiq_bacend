using Microsoft.EntityFrameworkCore;
using WA.Backend.Models;

namespace WA.Backend.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MemoryEntry> Memories => Set<MemoryEntry>();
    public DbSet<OwnerProfile> OwnerProfiles => Set<OwnerProfile>();
    public DbSet<Habit> Habits => Set<Habit>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Conversation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SessionId).IsUnique();
            e.HasMany(x => x.Messages)
             .WithOne(x => x.Conversation)
             .HasForeignKey(x => x.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Message>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
        });

        b.Entity<MemoryEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Key);
            e.HasIndex(x => x.Category);
        });

        b.Entity<OwnerProfile>(e => e.HasKey(x => x.Id));
        b.Entity<Habit>(e => e.HasKey(x => x.Id));
    }
}
