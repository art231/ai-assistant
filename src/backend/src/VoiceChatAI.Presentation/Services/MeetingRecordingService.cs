using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VoiceChatAI.Domain.Entities;
using VoiceChatAI.Domain.Interfaces;

namespace VoiceChatAI.Presentation.Services;

/// <summary>
/// Configuration options for meeting recording.
/// </summary>
public class RecordingOptions
{
    public const string SectionName = "Recording";
    public string OutputPath { get; set; } = "/app/recordings";
    public string AudioFormat { get; set; } = "ogg";
    public bool EnablePostProcessing { get; set; } = true;
}

/// <summary>
/// Manages meeting recording lifecycle: start/stop recording,
/// audio file management, and post-meeting transcription.
/// </summary>
public class MeetingRecordingService : BackgroundService
{
    private readonly RecordingOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeetingRecordingService> _logger;

    // Active recordings: RoomId -> RecordingSession
    private readonly ConcurrentDictionary<Guid, RecordingSession> _activeRecordings = new();

    public MeetingRecordingService(
        IOptions<RecordingOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<MeetingRecordingService> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Starts recording for a given room.
    /// </summary>
    public async Task<MeetingRecording> StartRecordingAsync(Guid roomId)
    {
        if (_activeRecordings.ContainsKey(roomId))
        {
            _logger.LogWarning("Recording already in progress for room {RoomId}", roomId);
            throw new InvalidOperationException($"Recording already in progress for room {roomId}");
        }

        // Ensure output directory exists
        var roomDir = Path.Combine(_options.OutputPath, roomId.ToString());
        Directory.CreateDirectory(roomDir);

        var audioPath = Path.Combine(roomDir, $"meeting_{roomId:N}.{_options.AudioFormat}");

        var recording = new MeetingRecording(roomId, audioPath);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMeetingRecordingRepository>();
        await repo.CreateAsync(recording);

        var session = new RecordingSession
        {
            Recording = recording,
            AudioFilePath = audioPath,
            StartedAt = DateTime.UtcNow,
            Buffer = new MemoryStream()
        };

        _activeRecordings[roomId] = session;

        _logger.LogInformation("Recording started for room {RoomId}, path: {Path}", roomId, audioPath);
        return recording;
    }

    /// <summary>
    /// Stops recording for a given room and triggers post-processing.
    /// </summary>
    public async Task<MeetingRecording> StopRecordingAsync(Guid roomId)
    {
        if (!_activeRecordings.TryRemove(roomId, out var session))
        {
            _logger.LogWarning("No active recording found for room {RoomId}", roomId);
            throw new InvalidOperationException($"No active recording for room {roomId}");
        }

        // Flush buffer to file
        await FlushBufferToFileAsync(session);

        var fileInfo = new FileInfo(session.AudioFilePath);
        session.Recording.StopRecording(fileInfo.Exists ? fileInfo.Length : 0);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMeetingRecordingRepository>();
        await repo.UpdateAsync(session.Recording);

        _logger.LogInformation("Recording stopped for room {RoomId}, size: {Size} bytes",
            roomId, session.Recording.FileSizeBytes);

        // Trigger post-processing (offline transcription)
        if (_options.EnablePostProcessing && fileInfo.Exists)
        {
            _ = Task.Run(() => PostProcessRecordingAsync(session.Recording));
        }

        return session.Recording;
    }

    /// <summary>
    /// Writes audio chunk to the active recording buffer.
    /// Called by AudioIngestionConsumer when receiving audio from Mediasoup.
    /// </summary>
    public async Task WriteAudioChunkAsync(Guid roomId, byte[] audioData)
    {
        if (_activeRecordings.TryGetValue(roomId, out var session))
        {
            await session.Buffer.WriteAsync(audioData);

            // Auto-flush every 10 seconds to avoid memory buildup
            if (session.Buffer.Length > 1024 * 1024) // 1MB threshold
            {
                await FlushBufferToFileAsync(session);
            }
        }
    }

    /// <summary>
    /// Gets the status of recording for a room.
    /// </summary>
    public bool IsRecording(Guid roomId) => _activeRecordings.ContainsKey(roomId);

    private async Task FlushBufferToFileAsync(RecordingSession session)
    {
        if (session.Buffer.Length == 0) return;

        try
        {
            await using var fileStream = new FileStream(
                session.AudioFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);

            session.Buffer.Position = 0;
            await session.Buffer.CopyToAsync(fileStream);
            await fileStream.FlushAsync();

            // Reset buffer
            session.Buffer.SetLength(0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush audio buffer to file: {Path}", session.AudioFilePath);
        }
    }

    /// <summary>
    /// Post-processes a completed recording: runs offline transcription
    /// and saves the full text to the database.
    /// </summary>
    private async Task PostProcessRecordingAsync(MeetingRecording recording)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMeetingRecordingRepository>();

            // Update status to processing
            recording = (await repo.GetByIdAsync(recording.Id))!;
            if (recording == null) return;

            // In production, this would call WhisperX or similar offline transcription.
            // For now, we simulate by concatenating existing real-time transcripts.
            var transcriptRepo = scope.ServiceProvider.GetRequiredService<ITranscriptRepository>();
            var transcripts = await transcriptRepo.GetRecentByRoomIdAsync(recording.RoomId, 10000);

            if (transcripts.Count > 0)
            {
                var fullText = string.Join("\n", transcripts.Select(t =>
                    $"[{t.Timestamp:HH:mm:ss}] {t.UserName}: {t.Text}"));

                recording.SetFullText(fullText);
                await repo.UpdateAsync(recording);
                _logger.LogInformation("Post-processing completed for recording {RecordingId}, {Count} transcripts",
                    recording.Id, transcripts.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-processing failed for recording {RecordingId}", recording.Id);
            recording.MarkFailed();

            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMeetingRecordingRepository>();
            await repo.UpdateAsync(recording);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // This service is event-driven via StartRecording/StopRecording calls.
        // No background loop needed.
        return Task.CompletedTask;
    }

    private class RecordingSession
    {
        public MeetingRecording Recording { get; set; } = null!;
        public string AudioFilePath { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public MemoryStream Buffer { get; set; } = null!;
    }
}
