namespace VoiceChatAI.Domain.Entities;

/// <summary>
/// Represents a transcribed text chunk from a participant's speech.
/// </summary>
public class Transcript
{
    public Guid Id { get; private set; }
    public Guid RoomId { get; private set; }
    public Guid? ParticipantId { get; private set; }
    public string SpeakerId { get; private set; } = "unknown";
    public string UserName { get; private set; } = null!;
    public string Text { get; private set; } = null!;
    public DateTime Timestamp { get; private set; }
    public bool IsFinal { get; private set; }
    public string Language { get; private set; } = null!;
    public float[]? Embedding { get; private set; }
    public string? Metadata { get; private set; } // JSON with voice metrics, etc.

    private Transcript() { } // EF Core

    public Transcript(Guid roomId, string userName, string text, Guid? participantId = null, bool isFinal = true, string language = "en", string speakerId = "unknown", string? metadata = null)
    {
        Id = Guid.NewGuid();
        RoomId = roomId;
        UserName = userName ?? throw new ArgumentNullException(nameof(userName));
        Text = text ?? throw new ArgumentNullException(nameof(text));
        ParticipantId = participantId;
        SpeakerId = speakerId;
        Metadata = metadata;
        Timestamp = DateTime.UtcNow;
        IsFinal = isFinal;
        Language = language;
    }

    public void SetEmbedding(float[] embedding)
    {
        Embedding = embedding ?? throw new ArgumentNullException(nameof(embedding));
    }

    public void SetFinal()
    {
        IsFinal = true;
    }
}
