using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Concierge.Shared.Chat;

/// <summary>
/// SQLite-backed store for conversations and messages. Single-file DB lives under
/// <c>%LocalAppData%/Concierge/concierge.db</c> on Windows (or the platform-equivalent
/// on macOS/Linux/MAUI). Schema is created via <c>EnsureCreated</c> at startup — a
/// proper migration history can come later when the schema starts evolving.
/// </summary>
public sealed class ConciergeChatDbContext : DbContext
{
    /// <summary>
    /// SQLite cannot ORDER BY <see cref="DateTimeOffset"/> directly. Storing as Unix
    /// milliseconds keeps ordering and indexing cheap, preserves UTC instants, and avoids
    /// timezone drift across machines that may write the same DB file.
    /// </summary>
    private static readonly ValueConverter<DateTimeOffset, long> UnixMillisecondsConverter =
        new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    public ConciergeChatDbContext(DbContextOptions<ConciergeChatDbContext> options)
        : base(options)
    {
    }

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessageRow> Messages => Set<ChatMessageRow>();

    /// <summary>
    /// The append-only log. Conversations and messages are projections of this; if the two
    /// ever disagree, the log is what is true.
    /// </summary>
    public DbSet<ConversationEvent> Events => Set<ConversationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.Property(c => c.CreatedAt).HasConversion(UnixMillisecondsConverter);
            entity.Property(c => c.UpdatedAt).HasConversion(UnixMillisecondsConverter);
            entity.HasIndex(c => new { c.OwnerId, c.UpdatedAt });
            entity.HasMany(c => c.Messages)
                .WithOne(m => m.Conversation!)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessageRow>(entity =>
        {
            entity.Property(m => m.CreatedAt).HasConversion(UnixMillisecondsConverter);
            entity.HasIndex(m => new { m.ConversationId, m.CreatedAt });
        });

        modelBuilder.Entity<ConversationEvent>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasConversion(UnixMillisecondsConverter);

            // Unique, not merely indexed: a duplicate sequence number is a corrupt log, and
            // the database is the only place that can refuse it under concurrent writers.
            entity.HasIndex(e => new { e.ConversationId, e.Seq }).IsUnique();
        });
    }
}
