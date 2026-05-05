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
            entity.Ignore(e => e.Participants);
        });

        // Participant configuration
        modelBuilder.Entity<Participant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserName).IsRequired().HasMaxLength(255);
            entity.HasIndex(e => e.RoomId);
        });

        // Transcript configuration
        modelBuilder.Entity<Transcript>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Text).IsRequired();
            entity.Property(e => e.Language).HasMaxLength(10);
            entity.Property(e => e.Embedding).HasColumnType("vector(1536)");
            entity.HasIndex(e => e.RoomId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.RoomId, e.Timestamp });
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
