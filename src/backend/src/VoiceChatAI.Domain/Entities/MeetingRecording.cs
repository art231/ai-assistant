namespace VoiceChatAI.Domain.Entities;

/// <summary>
/// Represents a recorded meeting with audio file and full transcript.
/// </summary>
public class MeetingRecording
{
    public Guid Id { get; private set; }
    public Guid RoomId { get; private set; }
    public string AudioPath { get; private set; } = null!;
    public string? FullText { get; private set; }
    public string? Summary { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public int DurationSeconds { get; private set; }
    public long FileSizeBytes { get; private set; }
    public RecordingStatus Status { get; private set; }

    private MeetingRecording() { } // EF Core

    public MeetingRecording(Guid roomId, string audioPath)
    {
        Id = Guid.NewGuid();
        RoomId = roomId;
        AudioPath = audioPath ?? throw new ArgumentNullException(nameof(audioPath));
        StartedAt = DateTime.UtcNow;
        Status = RecordingStatus.Recording;
    }

    public void StopRecording(long fileSizeBytes)
    {
        EndedAt = DateTime.UtcNow;
        DurationSeconds = (int)(EndedAt.Value - StartedAt).TotalSeconds;
        FileSizeBytes = fileSizeBytes;
        Status = RecordingStatus.Completed;
    }

    public void SetFullText(string fullText)
    {
        FullText = fullText ?? throw new ArgumentNullException(nameof(fullText));
    }

    public void SetSummary(string summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    public void MarkFailed()
    {
        Status = RecordingStatus.Failed;
        EndedAt = DateTime.UtcNow;
    }
}

public enum RecordingStatus
{
    Recording,
    Processing,
    Completed,
    Failed
}
