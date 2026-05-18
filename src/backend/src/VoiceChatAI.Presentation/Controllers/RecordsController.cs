using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceChatAI.Domain.Entities;
using VoiceChatAI.Domain.Interfaces;
using VoiceChatAI.Infrastructure.Data;
using VoiceChatAI.Infrastructure.Services;

namespace VoiceChatAI.Presentation.Controllers;

[ApiController]
[Route("api/recordings")]
public class RecordsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly WhisperService _whisperService;
    private readonly OllamaService _ollamaService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RecordsController> _logger;

    public RecordsController(
        AppDbContext context,
        WhisperService whisperService,
        OllamaService ollamaService,
        IServiceScopeFactory scopeFactory,
        ILogger<RecordsController> logger)
    {
        _context = context;
        _whisperService = whisperService;
        _ollamaService = ollamaService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/recordings/{roomId}/upload-audio — загрузить аудиофайл для обработки
    /// </summary>
    [HttpPost("{roomId:guid}/upload-audio")]
    [RequestSizeLimit(200 * 1024 * 1024)] // 200 MB
    public async Task<IActionResult> UploadAudio(Guid roomId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided or file is empty" });

        _logger.LogInformation("Uploading audio for room {RoomId}: {FileName} ({Size} bytes)",
            roomId, file.FileName, file.Length);

        // Find or create recording for this room
        var recording = await _context.MeetingRecordings
            .FirstOrDefaultAsync(r => r.RoomId == roomId);

        if (recording == null)
        {
            // Create a new recording entry
            var roomDir = Path.Combine("/app/recordings", roomId.ToString());
            Directory.CreateDirectory(roomDir);

            var audioPath = Path.Combine(roomDir, $"meeting_{roomId:N}.ogg");
            recording = new MeetingRecording(roomId, audioPath);
            _context.MeetingRecordings.Add(recording);
            await _context.SaveChangesAsync();
        }

        // Save the uploaded file
        var dir = Path.GetDirectoryName(recording.AudioPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await using (var stream = new FileStream(recording.AudioPath, FileMode.Create, FileAccess.Write))
        {
            await file.CopyToAsync(stream);
        }

        var fileInfo = new FileInfo(recording.AudioPath);
        recording.StopRecording(fileInfo.Length);

        _context.MeetingRecordings.Update(recording);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Audio saved for recording {RecordingId}: {Path} ({Size} bytes)",
            recording.Id, recording.AudioPath, fileInfo.Length);

        // Trigger post-processing (offline transcription) in a background task
        var recordingId = recording.Id;
        var audioFilePath = recording.AudioPath;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var whisperService = scope.ServiceProvider.GetRequiredService<WhisperService>();
                var ollamaService = scope.ServiceProvider.GetRequiredService<OllamaService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<RecordsController>>();

                logger.LogInformation("Starting post-processing for recording {RecordingId}", recordingId);

                // Reload recording from DB in the new scope
                var rec = await dbContext.MeetingRecordings.FirstOrDefaultAsync(r => r.Id == recordingId);
                if (rec == null)
                {
                    logger.LogWarning("Recording {RecordingId} not found for post-processing", recordingId);
                    return;
                }

                // Step 1: Transcribe via Whisper
                var transcription = await whisperService.TranscribeFileAsync(audioFilePath);
                var fullText = transcription.Text;

                if (!string.IsNullOrEmpty(fullText))
                {
                    if (transcription.Segments.Count > 0)
                    {
                        fullText = string.Join("\n", transcription.Segments.Select(s =>
                            $"[{TimeSpan.FromSeconds(s.Start):hh\\:mm\\:ss}] {s.Text}"));
                    }

                    rec.SetFullText(fullText);
                    logger.LogInformation("Whisper transcription completed: {Length} chars", fullText.Length);

                    // Step 2: Generate summary via Ollama
                    var summary = await ollamaService.GenerateSummaryAsync(fullText);
                    if (!string.IsNullOrEmpty(summary))
                    {
                        rec.SetSummary(summary);
                        logger.LogInformation("Summary generated: {Length} chars", summary.Length);
                    }
                }

                dbContext.MeetingRecordings.Update(rec);
                await dbContext.SaveChangesAsync();
                logger.LogInformation("Post-processing completed for recording {RecordingId}", recordingId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Post-processing failed for recording {RecordingId}", recordingId);
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var rec = await dbContext.MeetingRecordings.FirstOrDefaultAsync(r => r.Id == recordingId);
                    if (rec != null)
                    {
                        rec.MarkFailed();
                        dbContext.MeetingRecordings.Update(rec);
                        await dbContext.SaveChangesAsync();
                    }
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "Failed to mark recording {RecordingId} as failed", recordingId);
                }
            }
        });

        return Ok(new
        {
            recording.Id,
            recording.RoomId,
            recording.AudioPath,
            recording.FileSizeBytes,
            recording.Status,
            recording.StartedAt,
            recording.EndedAt,
            Message = "Audio uploaded. Post-processing started in background."
        });
    }
}
