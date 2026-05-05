using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using VoiceChatAI.Infrastructure.Data;

namespace VoiceChatAI.Presentation.Controllers;

[ApiController]
[Route("api/analytics")]
public class AnalyticsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<AnalyticsController> _logger;

    public AnalyticsController(AppDbContext context, ILogger<AnalyticsController> logger)
    {
        _context = context;
        _logger = logger;
        // QuestPDF license
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// GET /api/analytics/participant/{id} — статистика участника
    /// </summary>
    [HttpGet("participant/{id:guid}")]
    public async Task<ActionResult<ParticipantAnalyticsDto>> GetParticipantAnalytics(Guid id)
    {
        var participant = await _context.Participants.FindAsync(id);
        if (participant is null)
            return NotFound();

        // Count total words spoken by this participant
        var totalWords = await _context.Transcripts
            .Where(t => t.ParticipantId == id)
            .SumAsync(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        // Count total transcripts
        var transcriptCount = await _context.Transcripts
            .CountAsync(t => t.ParticipantId == id);

        // Count total meetings participated in
        var meetingCount = await _context.Participants
            .CountAsync(p => p.UserName == participant.UserName);

        // Total speaking time (estimated: each transcript ~3 seconds of audio)
        var estimatedSpeakingSeconds = transcriptCount * 3;

        return Ok(new ParticipantAnalyticsDto
        {
            ParticipantId = id,
            UserName = participant.UserName,
            TotalWords = totalWords,
            TotalTranscripts = transcriptCount,
            TotalMeetings = meetingCount,
            EstimatedSpeakingSeconds = estimatedSpeakingSeconds,
            AdviceGiven = await _context.Advice
                .Join(_context.Participants,
                    a => a.RoomId,
                    p => p.RoomId,
                    (a, p) => new { a, p })
                .Where(x => x.p.Id == id)
                .CountAsync(),
            ActivityPercentage = await CalculateActivityPercentageAsync(id)
        });
    }

    /// <summary>
    /// GET /api/analytics/room/{id} — сводка по комнате/встрече
    /// </summary>
    [HttpGet("room/{id:guid}")]
    public async Task<ActionResult<RoomAnalyticsDto>> GetRoomAnalytics(Guid id)
    {
        var room = await _context.Rooms.FindAsync(id);
        if (room is null)
            return NotFound();

        var participantCount = await _context.Participants
            .CountAsync(p => p.RoomId == id);

        var transcriptCount = await _context.Transcripts
            .CountAsync(t => t.RoomId == id);

        var totalWords = await _context.Transcripts
            .Where(t => t.RoomId == id)
            .SumAsync(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        var topicChanges = await _context.TopicChanges
            .CountAsync(tc => tc.RoomId == id);

        var adviceCount = await _context.Advice
            .CountAsync(a => a.RoomId == id);

        var duration = room.EndedAt.HasValue
            ? (int)(room.EndedAt.Value - room.CreatedAt).TotalSeconds
            : 0;

        // Top speakers
        var topSpeakers = await _context.Transcripts
            .Where(t => t.RoomId == id)
            .GroupBy(t => t.UserName)
            .Select(g => new SpeakerStatDto
            {
                UserName = g.Key,
                WordCount = g.Sum(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                MessageCount = g.Count()
            })
            .OrderByDescending(s => s.WordCount)
            .Take(5)
            .ToListAsync();

        return Ok(new RoomAnalyticsDto
        {
            RoomId = id,
            RoomName = room.Name,
            ParticipantCount = participantCount,
            TranscriptCount = transcriptCount,
            TotalWords = totalWords,
            DurationSeconds = duration,
            TopicChanges = topicChanges,
            AdviceCount = adviceCount,
            TopSpeakers = topSpeakers
        });
    }

    /// <summary>
    /// GET /api/analytics/meeting-efficiency — графики эффективности
    /// </summary>
    [HttpGet("meeting-efficiency")]
    public async Task<ActionResult<IEnumerable<MeetingEfficiencyDto>>> GetMeetingEfficiency(
        [FromQuery] int days = 30)
    {
        var since = DateTime.UtcNow.AddDays(-days);

        var rooms = await _context.Rooms
            .Where(r => r.CreatedAt >= since && r.Status == Domain.Entities.RoomStatus.Ended)
            .ToListAsync();

        var result = new List<MeetingEfficiencyDto>();

        foreach (var room in rooms)
        {
            var topicChanges = await _context.TopicChanges
                .CountAsync(tc => tc.RoomId == room.Id);

            var adviceCount = await _context.Advice
                .CountAsync(a => a.RoomId == room.Id);

            var participantCount = await _context.Participants
                .CountAsync(p => p.RoomId == room.Id);

            var duration = room.EndedAt.HasValue
                ? (int)(room.EndedAt.Value - room.CreatedAt).TotalSeconds
                : 0;

            result.Add(new MeetingEfficiencyDto
            {
                RoomId = room.Id,
                RoomName = room.Name,
                Date = room.CreatedAt,
                DurationMinutes = duration / 60,
                ParticipantCount = participantCount,
                TopicChanges = topicChanges,
                AdviceCount = adviceCount,
                EfficiencyScore = CalculateEfficiencyScore(duration, topicChanges, adviceCount, participantCount)
            });
        }

        return Ok(result.OrderByDescending(r => r.Date));
    }

    /// <summary>
    /// GET /api/recordings/{id}/export-pdf — экспорт конспекта в PDF
    /// </summary>
    [HttpGet("/api/recordings/{id:guid}/export-pdf")]
    public async Task<IActionResult> ExportRecordingPdf(Guid id)
    {
        var recording = await _context.MeetingRecordings
            .Include(r => r.RoomId) // just to verify it exists
            .FirstOrDefaultAsync(r => r.Id == id);

        if (recording is null)
            return NotFound();

        var room = await _context.Rooms.FindAsync(recording.RoomId);
        var roomName = room?.Name ?? "Unknown Room";

        // Build PDF document
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(11));

                page.Header().Element(c => ComposeHeader(c, roomName, recording));
                page.Content().Element(c => ComposeContent(c, recording));
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Generated by VoiceChatAI — ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

        var pdfBytes = document.GeneratePdf();
        return File(pdfBytes, "application/pdf", $"meeting_{roomName}_{recording.StartedAt:yyyy-MM-dd}.pdf");
    }

    private void ComposeHeader(IContainer container, string roomName, Domain.Entities.MeetingRecording recording)
    {
        container.Column(col =>
        {
            col.Item().Text($"Meeting: {roomName}")
                .FontSize(20).Bold().FontColor(Colors.Blue.Darken3);

            col.Item().Text($"Date: {recording.StartedAt:yyyy-MM-dd HH:mm} UTC")
                .FontSize(12).FontColor(Colors.Grey.Darken2);

            col.Item().Text($"Duration: {recording.DurationSeconds / 60} min {recording.DurationSeconds % 60} sec")
                .FontSize(12).FontColor(Colors.Grey.Darken2);

            if (!string.IsNullOrEmpty(recording.Summary))
            {
                col.Item().PaddingTop(10).Text("Summary").FontSize(16).Bold();
                col.Item().Text(recording.Summary).FontSize(11);
            }

            col.Item().PaddingBottom(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
        });
    }

    private void ComposeContent(IContainer container, Domain.Entities.MeetingRecording recording)
    {
        container.Column(col =>
        {
            col.Item().Text("Full Transcript").FontSize(16).Bold();

            if (!string.IsNullOrEmpty(recording.FullText))
            {
                // Split transcript into lines and format
                var lines = recording.FullText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    col.Item().PaddingVertical(2).Text(line).FontSize(10);

                    // Extract speaker name if present
                    var colonIndex = line.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        var speaker = line[..colonIndex].Trim();
                        col.Item().Text(speaker).FontSize(9).FontColor(Colors.Blue.Darken2);
                    }
                }
            }
            else
            {
                col.Item().Text("No transcript available.").FontSize(11).FontColor(Colors.Grey.Darken1);
            }
        });
    }

    private async Task<double> CalculateActivityPercentageAsync(Guid participantId)
    {
        // Calculate what percentage of the meeting time the participant was speaking
        var participant = await _context.Participants.FindAsync(participantId);
        if (participant is null) return 0;

        var roomTranscripts = await _context.Transcripts
            .CountAsync(t => t.RoomId == participant.RoomId);

        if (roomTranscripts == 0) return 0;

        var participantTranscripts = await _context.Transcripts
            .CountAsync(t => t.ParticipantId == participantId);

        return Math.Round((double)participantTranscripts / roomTranscripts * 100, 1);
    }

    private static double CalculateEfficiencyScore(int durationSeconds, int topicChanges, int adviceCount, int participantCount)
    {
        // Simple heuristic: more advice + balanced participation = higher efficiency
        // Ideal: 30-60 min, 3-8 topic changes, 5+ advice items, 3-10 participants
        if (durationSeconds == 0) return 0;

        var durationMinutes = durationSeconds / 60.0;
        var score = 50.0; // base

        // Duration bonus (ideal 30-60 min)
        if (durationMinutes >= 20 && durationMinutes <= 90) score += 15;
        else if (durationMinutes < 10) score -= 20;

        // Topic changes (healthy discussion)
        score += Math.Min(topicChanges * 3, 15);

        // Advice generated
        score += Math.Min(adviceCount * 5, 15);

        // Participation
        if (participantCount >= 3 && participantCount <= 15) score += 5;

        return Math.Round(Math.Min(Math.Max(score, 0), 100), 1);
    }
}

// ─── DTOs ────────────────────────────────────────────────────────

public class ParticipantAnalyticsDto
{
    public Guid ParticipantId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public int TotalWords { get; set; }
    public int TotalTranscripts { get; set; }
    public int TotalMeetings { get; set; }
    public int EstimatedSpeakingSeconds { get; set; }
    public int AdviceGiven { get; set; }
    public double ActivityPercentage { get; set; }
}

public class RoomAnalyticsDto
{
    public Guid RoomId { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public int ParticipantCount { get; set; }
    public int TranscriptCount { get; set; }
    public int TotalWords { get; set; }
    public int DurationSeconds { get; set; }
    public int TopicChanges { get; set; }
    public int AdviceCount { get; set; }
    public List<SpeakerStatDto> TopSpeakers { get; set; } = new();
}

public class SpeakerStatDto
{
    public string UserName { get; set; } = string.Empty;
    public int WordCount { get; set; }
    public int MessageCount { get; set; }
}

public class MeetingEfficiencyDto
{
    public Guid RoomId { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public int DurationMinutes { get; set; }
    public int ParticipantCount { get; set; }
    public int TopicChanges { get; set; }
    public int AdviceCount { get; set; }
    public double EfficiencyScore { get; set; }
}
