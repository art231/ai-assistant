using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VoiceChatAI.Domain.Entities;
using VoiceChatAI.Domain.Interfaces;
using VoiceChatAI.Infrastructure.Data;

namespace VoiceChatAI.Presentation.Controllers;

[ApiController]
[Route("api")]
public class SearchController : ControllerBase
{
    private readonly IMeetingRecordingRepository _recordingRepo;
    private readonly AppDbContext _context;
    private readonly ILogger<SearchController> _logger;

    public SearchController(
        IMeetingRecordingRepository recordingRepo,
        AppDbContext context,
        ILogger<SearchController> logger)
    {
        _recordingRepo = recordingRepo;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Full-text search across meeting recordings.
    /// GET /api/search?q=keyword
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<SearchResultDto>>> Search(
        [FromQuery] string q,
        [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<SearchResultDto>());

        _logger.LogInformation("Search request: {Query}", q);

        // Full-text search using PostgreSQL tsvector
        var results = await _context.MeetingRecordings
            .FromSqlRaw(@"
                SELECT * FROM ""MeetingRecordings""
                WHERE ""FullText"" IS NOT NULL
                AND to_tsvector('english', ""FullText"") @@ plainto_tsquery('english', {0})
                ORDER BY ts_rank(to_tsvector('english', ""FullText""), plainto_tsquery('english', {0})) DESC
                LIMIT {1}",
                q, limit)
            .Select(r => new SearchResultDto
            {
                RecordingId = r.Id,
                RoomId = r.RoomId,
                TranscriptSnippet = r.FullText!.Length > 200
                    ? r.FullText.Substring(0, 200) + "..."
                    : r.FullText,
                StartedAt = r.StartedAt,
                EndedAt = r.EndedAt,
                DurationSeconds = r.DurationSeconds,
                AudioPath = r.AudioPath
            })
            .ToListAsync();

        // Enrich with room names
        var roomIds = results.Select(r => r.RoomId).Distinct().ToList();
        var roomNames = await _context.Rooms
            .Where(room => roomIds.Contains(room.Id))
            .ToDictionaryAsync(room => room.Id, room => room.Name);

        foreach (var result in results)
        {
            result.RoomName = roomNames.GetValueOrDefault(result.RoomId, "Unknown Room");
        }

        return Ok(results);
    }

    /// <summary>
    /// Get all recordings for a room.
    /// GET /api/recordings?roomId=...
    /// </summary>
    [HttpGet("recordings")]
    public async Task<ActionResult<IEnumerable<RecordingDto>>> GetRecordings(
        [FromQuery] Guid? roomId)
    {
        IEnumerable<MeetingRecording> recordings;

        if (roomId.HasValue)
        {
            recordings = await _recordingRepo.GetByRoomIdAsync(roomId.Value);
        }
        else
        {
            recordings = await _context.MeetingRecordings
                .OrderByDescending(r => r.StartedAt)
                .Take(50)
                .ToListAsync();
        }

        var dtos = recordings.Select(r => new RecordingDto
        {
            Id = r.Id,
            RoomId = r.RoomId,
            AudioPath = r.AudioPath,
            Transcript = r.FullText,
            Summary = r.Summary,
            StartedAt = r.StartedAt,
            EndedAt = r.EndedAt,
            DurationSeconds = r.DurationSeconds,
            FileSizeBytes = r.FileSizeBytes,
            Status = r.Status.ToString()
        });

        return Ok(dtos);
    }

    /// <summary>
    /// Get a specific recording by ID.
    /// GET /api/recordings/{id}
    /// </summary>
    [HttpGet("recordings/{id:guid}")]
    public async Task<ActionResult<RecordingDto>> GetRecording(Guid id)
    {
        var recording = await _recordingRepo.GetByIdAsync(id);
        if (recording is null)
            return NotFound();

        return Ok(new RecordingDto
        {
            Id = recording.Id,
            RoomId = recording.RoomId,
            AudioPath = recording.AudioPath,
            Transcript = recording.FullText,
            Summary = recording.Summary,
            StartedAt = recording.StartedAt,
            EndedAt = recording.EndedAt,
            DurationSeconds = recording.DurationSeconds,
            FileSizeBytes = recording.FileSizeBytes,
            Status = recording.Status.ToString()
        });
    }

    /// <summary>
    /// Stream audio file for a recording.
    /// GET /api/recordings/{id}/audio
    /// </summary>
    [HttpGet("recordings/{id:guid}/audio")]
    public async Task<IActionResult> GetRecordingAudio(Guid id)
    {
        var recording = await _recordingRepo.GetByIdAsync(id);
        if (recording is null)
            return NotFound();

        if (!System.IO.File.Exists(recording.AudioPath))
            return NotFound("Audio file not found");

        var stream = new FileStream(recording.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, "audio/ogg", $"meeting_{id:N}.ogg");
    }
}

// DTOs for search and recordings
public class SearchResultDto
{
    public Guid RecordingId { get; set; }
    public Guid RoomId { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public string? TranscriptSnippet { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    public string? AudioPath { get; set; }
}

public class RecordingDto
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public string AudioPath { get; set; } = string.Empty;
    public string? Transcript { get; set; }
    public string? Summary { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    public long FileSizeBytes { get; set; }
    public string Status { get; set; } = string.Empty;
}
