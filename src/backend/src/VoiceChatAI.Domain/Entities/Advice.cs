namespace VoiceChatAI.Domain.Entities;

/// <summary>
/// Represents an AI-generated advice, summary, or alternative idea during a meeting.
/// </summary>
public class Advice
{
    public Guid Id { get; private set; }
    public Guid RoomId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public AdviceType Type { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public int Upvotes { get; private set; }

    private Advice() { } // EF Core

    public Advice(Guid roomId, string text, AdviceType type)
    {
        Id = Guid.NewGuid();
        RoomId = roomId;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Type = type;
        CreatedAt = DateTime.UtcNow;
        Upvotes = 0;
    }

    public void Upvote()
    {
        Upvotes++;
    }

    public void Downvote()
    {
        if (Upvotes > 0) Upvotes--;
    }
}

public enum AdviceType
{
    Summary,
    TopicChange,
    Advice,
    AlternativeIdea
}
