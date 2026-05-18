using Microsoft.EntityFrameworkCore;
using VoiceChatAI.Domain.Entities;

namespace VoiceChatAI.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<Transcript> Transcripts => Set<Transcript>();
    public DbSet<MeetingRecording> MeetingRecordings => Set<MeetingRecording>();
    public DbSet<TopicChange> TopicChanges => Set<TopicChange>();
    public DbSet<Advice> Advice => Set<Advice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Room configuration
        modelBuilder.Entity<Room>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50)
                .HasConversion<string>();
            entity.Property(e => e.Metadata).HasColumnType("jsonb");

            // Map Participants navigation to _participants backing field
            entity.HasMany(e => e.ParticipantsNavigation)
                  .WithOne(p => p.Room)
                  .HasForeignKey(p => p.RoomId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Participant configuration
        modelBuilder.Entity<Participant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.RoomId).IsRequired();
            entity.HasIndex(e => e.RoomId);
        });

        // Transcript configuration
        modelBuilder.Entity<Transcript>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Text).IsRequired();
            entity.Property(e => e.Language).HasMaxLength(10);
            entity.Property(e => e.SpeakerId).HasMaxLength(50);
            entity.Property(e => e.Metadata).HasColumnType("jsonb");
            entity.Property(e => e.Embedding)
                .HasColumnType("text")
                .HasConversion(
                    v => v == null ? null : string.Join(',', v),
                    v => v == null ? null : v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(float.Parse).ToArray()
                );
            entity.HasIndex(e => e.RoomId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.RoomId, e.Timestamp });
            entity.HasIndex(e => e.SpeakerId);
        });

        // MeetingRecording configuration
        modelBuilder.Entity<MeetingRecording>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AudioPath).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50)
                .HasConversion<string>();
            entity.HasIndex(e => e.RoomId);
            entity.HasIndex(e => e.Status);
        });

        // TopicChange configuration
        modelBuilder.Entity<TopicChange>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OldTopic).IsRequired().HasMaxLength(500);
            entity.Property(e => e.NewTopic).IsRequired().HasMaxLength(500);
            entity.HasIndex(e => e.RoomId);
            entity.HasIndex(e => e.DetectedAt);
        });

        // Advice configuration
        modelBuilder.Entity<Advice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Text).IsRequired();
            entity.Property(e => e.Type).IsRequired().HasMaxLength(50)
                .HasConversion<string>();
            entity.HasIndex(e => e.RoomId);
            entity.HasIndex(e => e.CreatedAt);
        });
    }
}
