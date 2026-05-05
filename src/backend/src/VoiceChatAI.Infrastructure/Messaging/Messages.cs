namespace VoiceChatAI.Infrastructure.Messaging;

/// <summary>
/// Message models for RabbitMQ communication between services.
/// </summary>

public record TranscriptMessage
{
    public Guid RoomId { get; init; }
    public Guid? ParticipantId { get; init; }
    public string UserName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public bool IsFinal { get; init; } = true;
    public string Language { get; init; } = "en";
    public DateTime Timestamp { get; init; }
}

public record SummaryRequestMessage
{
    public Guid RoomId { get; init; }
    public DateTime RequestedAt { get; init; }
}
