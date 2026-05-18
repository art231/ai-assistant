namespace VoiceChatAI.Domain.Entities;

/// <summary>
/// Represents a participant in a meeting room.
/// </summary>
public class Participant
{
    public Guid Id { get; private set; }
    public Guid RoomId { get; set; }
    // EF Core navigation property
    public Room? Room { get; set; }
    public string UserName { get; private set; } = null!;
    public DateTime JoinedAt { get; private set; }
    public DateTime? LeftAt { get; private set; }
    public bool IsSpeaking { get; private set; }
    public float AudioLevel { get; private set; }

    private Participant() { } // EF Core

    public Participant(Guid roomId, string userName)
    {
        Id = Guid.NewGuid();
        RoomId = roomId;
        UserName = userName ?? throw new ArgumentNullException(nameof(userName));
        JoinedAt = DateTime.UtcNow;
        IsSpeaking = false;
        AudioLevel = 0.0f;
    }

    public void MarkLeft()
    {
        LeftAt = DateTime.UtcNow;
        IsSpeaking = false;
        AudioLevel = 0.0f;
    }

    public void SetSpeaking(bool isSpeaking, float audioLevel = 0.0f)
    {
        IsSpeaking = isSpeaking;
        AudioLevel = audioLevel;
    }

    public bool IsActive => LeftAt == null;
}
