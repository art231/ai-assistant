namespace VoiceChatAI.Domain.Entities;

/// <summary>
/// Represents a detected topic change during a meeting.
/// </summary>
public class TopicChange
{
    public Guid Id { get; private set; }
    public Guid RoomId { get; private set; }
    public string OldTopic { get; private set; } = string.Empty;
    public string NewTopic { get; private set; } = string.Empty;
    public DateTime DetectedAt { get; private set; }
    public double Confidence { get; private set; }

    private TopicChange() { } // EF Core

    public TopicChange(Guid roomId, string oldTopic, string newTopic, double confidence = 0.8)
    {
        Id = Guid.NewGuid();
        RoomId = roomId;
        OldTopic = oldTopic ?? throw new ArgumentNullException(nameof(oldTopic));
        NewTopic = newTopic ?? throw new ArgumentNullException(nameof(newTopic));
        DetectedAt = DateTime.UtcNow;
        Confidence = Math.Clamp(confidence, 0.0, 1.0);
    }
}
