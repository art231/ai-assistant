namespace VoiceChatAI.Application.DTOs;

public record RoomDto(
    Guid Id,
    string Name,
    string Status,
    int MaxParticipants,
    int ParticipantCount,
    DateTime CreatedAt,
    DateTime? EndedAt
);

public record CreateRoomDto(string Name, int MaxParticipants = 20);

public record ParticipantDto(
    Guid Id,
    string UserName,
    DateTime JoinedAt,
    bool IsSpeaking,
    float AudioLevel
);

public record TranscriptDto(
    Guid Id,
    Guid RoomId,
    string UserName,
    string Text,
    DateTime Timestamp,
    bool IsFinal
);

public record MeetingRecordingDto(
    Guid Id,
    Guid RoomId,
    string AudioPath,
    string? FullText,
    string? Summary,
    DateTime StartedAt,
    DateTime? EndedAt,
    int DurationSeconds,
    string Status
);

public record SearchResultDto(
    Guid RecordingId,
    Guid RoomId,
    string RoomName,
    string TextSnippet,
    DateTime Timestamp,
    int OffsetSeconds
);

public record MeetingSummaryDto(
    Guid Id,
    string SummaryText,
    string[] Topics,
    DateTime GeneratedAt
);

public record AdviceDto(
    Guid Id,
    string Type,
    string Text,
    DateTime GeneratedAt
);

public record TopicChangeDto(
    Guid Id,
    string? OldTopic,
    string NewTopic,
    DateTime DetectedAt,
    float Confidence
);
