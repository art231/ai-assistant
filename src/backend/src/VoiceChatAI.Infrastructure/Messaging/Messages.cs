namespace VoiceChatAI.Infrastructure.Messaging;

/// <summary>
/// Message models for RabbitMQ communication between services.
/// </summary>

public record TranscriptMessage
{
    public Guid RoomId { get; init; }
    public Guid? ParticipantId { get; init; }
    public string SpeakerId { get; init; } = "unknown";
    public string UserName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public bool IsFinal { get; init; } = true;
    public string Language { get; init; } = "en";
    public DateTime Timestamp { get; init; }
    public VoiceMetricsDto? VoiceMetrics { get; init; }
}

public record VoiceMetricsDto
{
    public string Gender { get; init; } = "unknown";
    public double GenderConfidence { get; init; }
    public string Emotion { get; init; } = "unknown";
    public double EmotionConfidence { get; init; }
    public double FatigueLevel { get; init; }
    public List<string> FatigueIndicators { get; init; } = new();
    public double SpeechRate { get; init; }
    public double PitchVariability { get; init; }
}

public record SummaryRequestMessage
{
    public Guid RoomId { get; init; }
    public DateTime RequestedAt { get; init; }
}
