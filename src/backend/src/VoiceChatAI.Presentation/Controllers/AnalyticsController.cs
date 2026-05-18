using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using VoiceChatAI.Domain.Entities;
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
        QuestPDF.Settings.License = LicenseType.Community;
    }

    // ─── Внутренний класс для статистики спикеров ──────────────────────
    private sealed class SpeakerStat
    {
        public string UserName { get; set; } = "";
        public int WordCount { get; set; }
        public int MessageCount { get; set; }
    }

    // ─── Экспорт PDF ───────────────────────────────────────────────────

    /// <summary>
    /// GET /api/analytics/room/{roomId}/export-pdf — экспорт отчёта встречи в PDF (русский язык)
    /// </summary>
    [HttpGet("room/{roomId:guid}/export-pdf")]
    public async Task<IActionResult> ExportRoomPdf(Guid roomId)
    {
        var room = await _context.Rooms
            .Include(r => r.ParticipantsNavigation)
            .FirstOrDefaultAsync(r => r.Id == roomId);

        if (room is null)
            return NotFound(new { error = "Комната не найдена" });

        // Собираем данные
        var transcripts = await _context.Transcripts
            .Where(t => t.RoomId == roomId)
            .OrderBy(t => t.Timestamp)
            .ToListAsync();

        var adviceList = await _context.Advice
            .Where(a => a.RoomId == roomId)
            .ToListAsync();

        var topicChanges = await _context.TopicChanges
            .Where(tc => tc.RoomId == roomId)
            .OrderBy(tc => tc.DetectedAt)
            .ToListAsync();

        var recording = await _context.MeetingRecordings
            .FirstOrDefaultAsync(r => r.RoomId == roomId);

        // Статистика
        var participantCount = room.ParticipantsNavigation.Count;
        var transcriptCount = transcripts.Count;
        var totalWords = transcripts.Sum(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var duration = room.EndedAt.HasValue
            ? (int)(room.EndedAt.Value - room.CreatedAt).TotalSeconds
            : 0;

        // Топ-спикеры
        var speakerStats = transcripts
            .GroupBy(t => t.UserName)
            .Select(g => new SpeakerStat
            {
                UserName = g.Key,
                WordCount = g.Sum(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                MessageCount = g.Count()
            })
            .OrderByDescending(s => s.WordCount)
            .ToList();

        // Voice metrics по спикерам
        var voiceMetricsBySpeaker = new Dictionary<string, List<Dictionary<string, object>>>();
        foreach (var t in transcripts.Where(t => !string.IsNullOrEmpty(t.Metadata)))
        {
            try
            {
                var metrics = JsonSerializer.Deserialize<Dictionary<string, object>>(t.Metadata);
                if (metrics != null)
                {
                    if (!voiceMetricsBySpeaker.ContainsKey(t.UserName))
                        voiceMetricsBySpeaker[t.UserName] = new();
                    voiceMetricsBySpeaker[t.UserName].Add(metrics);
                }
            }
            catch { }
        }

        _logger.LogInformation(
            "Экспорт PDF для комнаты {RoomId}, {RoomName}. " +
            "Участников: {Participants}, Транскриптов: {Transcripts}, Слов: {Words}",
            roomId, room.Name, participantCount, transcriptCount, totalWords);

        // Генерируем PDF
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(11));

                page.Header().Element(c => ComposeHeader(c, room, recording, duration));
                page.Content().Element(c => ComposeContent(c, room, transcripts, adviceList, topicChanges,
                    speakerStats, voiceMetricsBySpeaker, recording, duration));
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Сгенерировано VoiceChatAI — ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

        var pdfBytes = document.GeneratePdf();
        _logger.LogInformation("PDF сгенерирован для комнаты {RoomId}: {Size} байт", roomId, pdfBytes.Length);

        var fileName = $"отчёт_{room.Name}_{room.CreatedAt:yyyy-MM-dd}.pdf";
        return File(pdfBytes, "application/pdf", fileName);
    }

    // ─── Компоновка PDF ────────────────────────────────────────────────

    private void ComposeHeader(IContainer container, Room room, MeetingRecording? recording, int durationSeconds)
    {
        container.Column(col =>
        {
            col.Item().Text($"Отчёт о встрече: {room.Name}")
                .FontSize(20).Bold().FontColor(Colors.Blue.Darken3);

            col.Item().Text($"Дата: {room.CreatedAt:dd.MM.yyyy HH:mm} UTC")
                .FontSize(12).FontColor(Colors.Grey.Darken2);

            var minutes = durationSeconds / 60;
            var seconds = durationSeconds % 60;
            col.Item().Text($"Длительность: {minutes} мин {seconds} сек")
                .FontSize(12).FontColor(Colors.Grey.Darken2);

            col.Item().Text("Статус: Завершена")
                .FontSize(12).FontColor(Colors.Green.Darken2);

            if (recording?.FileSizeBytes > 0)
            {
                var sizeKb = recording.FileSizeBytes / 1024.0;
                col.Item().Text($"Размер аудиозаписи: {sizeKb:F1} КБ")
                    .FontSize(12).FontColor(Colors.Grey.Darken2);
            }

            col.Item().PaddingBottom(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
        });
    }

    private void ComposeContent(IContainer container, Room room, List<Transcript> transcripts,
        List<Advice> adviceList, List<TopicChange> topicChanges,
        List<SpeakerStat> speakerStats, Dictionary<string, List<Dictionary<string, object>>> voiceMetricsBySpeaker,
        MeetingRecording? recording, int durationSeconds)
    {
        container.Column(col =>
        {
            ComposeMeetingStats(col, room, transcripts, adviceList, topicChanges, durationSeconds);
            ComposeTopSpeakers(col, speakerStats, transcripts.Count);

            if (recording != null && !string.IsNullOrEmpty(recording.Summary))
            {
                col.Item().PaddingTop(15).Text("Краткое содержание").FontSize(16).Bold();
                col.Item().PaddingTop(5).Text(recording.Summary).FontSize(11);
            }

            if (voiceMetricsBySpeaker.Count > 0)
                ComposeVoiceMetrics(col, voiceMetricsBySpeaker);

            if (topicChanges.Count > 0)
                ComposeTopicChanges(col, topicChanges);

            if (adviceList.Count > 0)
                ComposeAdvice(col, adviceList);

            ComposeTranscript(col, transcripts);
        });
    }

    private void ComposeMeetingStats(ColumnDescriptor col, Room room, List<Transcript> transcripts,
        List<Advice> adviceList, List<TopicChange> topicChanges, int durationSeconds)
    {
        col.Item().PaddingTop(10).Text("Статистика встречи").FontSize(16).Bold();

        var participantCount = room.ParticipantsNavigation.Count;
        var totalWords = transcripts.Sum(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var minutes = durationSeconds / 60;

        col.Item().PaddingTop(8).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn();
                columns.RelativeColumn();
                columns.RelativeColumn();
                columns.RelativeColumn();
            });

            table.Header(header =>
            {
                header.Cell().Background(Colors.Blue.Darken3).Padding(5)
                    .Text("Участники").FontColor(Colors.White).FontSize(10).Bold();
                header.Cell().Background(Colors.Blue.Darken3).Padding(5)
                    .Text("Сообщения").FontColor(Colors.White).FontSize(10).Bold();
                header.Cell().Background(Colors.Blue.Darken3).Padding(5)
                    .Text("Слов").FontColor(Colors.White).FontSize(10).Bold();
                header.Cell().Background(Colors.Blue.Darken3).Padding(5)
                    .Text("Длит.").FontColor(Colors.White).FontSize(10).Bold();
            });

            table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
                .Text(participantCount.ToString()).FontSize(11).AlignCenter();
            table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
                .Text(transcripts.Count.ToString()).FontSize(11).AlignCenter();
            table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
                .Text(totalWords.ToString()).FontSize(11).AlignCenter();
            table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
                .Text($"{minutes} мин").FontSize(11).AlignCenter();
        });

        col.Item().PaddingTop(5).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn();
                columns.RelativeColumn();
            });

            table.Header(header =>
            {
                header.Cell().Background(Colors.Blue.Darken3).Padding(5)
                    .Text("Смены тем").FontColor(Colors.White).FontSize(10).Bold();
                header.Cell().Background(Colors.Blue.Darken3).Padding(5)
                    .Text("Рекомендации").FontColor(Colors.White).FontSize(10).Bold();
            });

            table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
                .Text(topicChanges.Count.ToString()).FontSize(11).AlignCenter();
            table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
                .Text(adviceList.Count.ToString()).FontSize(11).AlignCenter();
        });
    }

    private void ComposeTopSpeakers(ColumnDescriptor col, List<SpeakerStat> speakerStats, int totalTranscripts)
    {
        if (speakerStats.Count == 0) return;

        col.Item().PaddingTop(15).Text("Активность участников").FontSize(16).Bold();

        col.Item().PaddingTop(8).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn();
                columns.RelativeColumn();
                columns.RelativeColumn();
                columns.RelativeColumn();
            });

            table.Header(header =>
            {
                header.Cell().Background(Colors.Blue.Darken3).Padding(5)
                    .Text("Участник").FontColor(Colors.White).FontSize(10).Bold();
                header.Cell().Background(Colors.Blue.Darken3).Padding(5)
                    .Text("Сообщений").FontColor(Colors.White).FontSize(10).Bold();
                header.Cell().Background(Colors.Blue.Darken3).Padding(5)
                    .Text("Слов").FontColor(Colors.White).FontSize(10).Bold();
                header.Cell().Background(Colors.Blue.Darken3).Padding(5)
                    .Text("Активность").FontColor(Colors.White).FontSize(10).Bold();
            });

            foreach (var speaker in speakerStats)
            {
                double activityPct = totalTranscripts > 0
                    ? Math.Round((double)speaker.MessageCount / totalTranscripts * 100, 1)
                    : 0;

                table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
                    .Text(speaker.UserName).FontSize(11);
                table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
                    .Text(speaker.MessageCount.ToString()).FontSize(11).AlignCenter();
                table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
                    .Text(speaker.WordCount.ToString()).FontSize(11).AlignCenter();
                table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5)
                    .Text($"{activityPct}%").FontSize(11).AlignCenter();
            }
        });
    }

    private void ComposeVoiceMetrics(ColumnDescriptor col,
        Dictionary<string, List<Dictionary<string, object>>> voiceMetricsBySpeaker)
    {
        col.Item().PaddingTop(15).Text("Анализ голоса").FontSize(16).Bold();

        foreach (var (speaker, metricsList) in voiceMetricsBySpeaker)
        {
            if (metricsList.Count == 0) continue;

            var genderCounts = new Dictionary<string, int>();
            var emotionCounts = new Dictionary<string, int>();
            double avgFatigue = 0;
            double avgSpeechRate = 0;

            foreach (var m in metricsList)
            {
                if (m.TryGetValue("gender", out var g))
                {
                    var key = g?.ToString() ?? "";
                    genderCounts[key] = genderCounts.GetValueOrDefault(key) + 1;
                }
                if (m.TryGetValue("emotion", out var e))
                {
                    var key = e?.ToString() ?? "";
                    emotionCounts[key] = emotionCounts.GetValueOrDefault(key) + 1;
                }
                if (m.TryGetValue("fatigueLevel", out var f) && f is JsonElement fe && fe.ValueKind == JsonValueKind.Number)
                    avgFatigue += fe.GetDouble();
                if (m.TryGetValue("speechRate", out var sr) && sr is JsonElement sre && sre.ValueKind == JsonValueKind.Number)
                    avgSpeechRate += sre.GetDouble();
            }

            avgFatigue = metricsList.Count > 0 ? avgFatigue / metricsList.Count : 0;
            avgSpeechRate = metricsList.Count > 0 ? avgSpeechRate / metricsList.Count : 0;

            var dominantGender = genderCounts.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "неизвестно";
            var dominantEmotion = emotionCounts.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "неизвестно";

            var genderIcon = dominantGender == "male" ? "♂" : dominantGender == "female" ? "♀" : "—";
            var fatiguePct = (avgFatigue * 100).ToString("F0");

            col.Item().PaddingTop(8).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(8).Column(inner =>
            {
                inner.Item().Text($"{speaker} {genderIcon}").FontSize(12).Bold();
                inner.Item().Text($"Преобладающая эмоция: {TranslateEmotion(dominantEmotion)}").FontSize(10);
                inner.Item().Text($"Уровень усталости: {fatiguePct}%").FontSize(10);
                inner.Item().Text($"Темп речи: {avgSpeechRate:F1} слогов/сек").FontSize(10);
            });
        }
    }

    private void ComposeTopicChanges(ColumnDescriptor col, List<TopicChange> topicChanges)
    {
        col.Item().PaddingTop(15).Text("Смены тем").FontSize(16).Bold();

        foreach (var tc in topicChanges)
        {
            col.Item().PaddingTop(5).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(8).Row(row =>
            {
                row.AutoItem().Text($"[{tc.DetectedAt:HH:mm:ss}]").FontSize(10).FontColor(Colors.Grey.Darken2);
                row.RelativeItem().PaddingLeft(8).Text(tc.NewTopic).FontSize(11);
            });
        }
    }

    private void ComposeAdvice(ColumnDescriptor col, List<Advice> adviceList)
    {
        col.Item().PaddingTop(15).Text("Рекомендации").FontSize(16).Bold();

        foreach (var advice in adviceList)
        {
            col.Item().PaddingTop(5).Border(1).BorderColor(Colors.Green.Lighten2).Padding(8).Column(inner =>
            {
                inner.Item().Text($"💡 {advice.Text}").FontSize(11);
                inner.Item().PaddingTop(3).Text($"Тип: {TranslateAdviceType(advice.Type)}")
                    .FontSize(9).FontColor(Colors.Grey.Darken2);
            });
        }
    }

    private void ComposeTranscript(ColumnDescriptor col, List<Transcript> transcripts)
    {
        if (transcripts.Count == 0) return;

        col.Item().PaddingTop(15).Text("Полный транскрипт").FontSize(16).Bold();

        foreach (var t in transcripts)
        {
            col.Item().PaddingTop(4).Border(1).BorderColor(Colors.Grey.Lighten3).Padding(6).Column(inner =>
            {
                inner.Item().Row(row =>
                {
                    row.AutoItem().Text($"[{t.Timestamp:HH:mm:ss}]").FontSize(9).FontColor(Colors.Grey.Darken2);
                    row.AutoItem().PaddingLeft(8).Text(t.UserName).FontSize(10).Bold().FontColor(Colors.Blue.Darken2);
                    if (t.SpeakerId != "unknown")
                    {
                        row.AutoItem().PaddingLeft(4).Text($"({t.SpeakerId})").FontSize(9).FontColor(Colors.Grey.Darken2);
                    }
                });
                inner.Item().PaddingTop(2).Text(t.Text).FontSize(10);
            });
        }
    }

    // ─── Вспомогательные методы ────────────────────────────────────────

    private static string TranslateEmotion(string emotion)
    {
        return emotion.ToLower() switch
        {
            "neutral" => "нейтральное",
            "happy" => "радость",
            "sad" => "грусть",
            "angry" => "гнев",
            "fearful" => "страх",
            "disgusted" => "отвращение",
            "surprised" => "удивление",
            _ => emotion
        };
    }

    private static string TranslateAdviceType(AdviceType type)
    {
        return type switch
        {
            AdviceType.Summary => "Сводка",
            AdviceType.TopicChange => "Смена темы",
            AdviceType.Advice => "Рекомендация",
            AdviceType.AlternativeIdea => "Альтернативная идея",
            _ => type.ToString()
        };
    }

    // ─── Существующие эндпоинты ────────────────────────────────────────

    [HttpGet("participant/{id:guid}")]
    public async Task<ActionResult<ParticipantAnalyticsDto>> GetParticipantAnalytics(Guid id)
    {
        var participant = await _context.Participants.FindAsync(id);
        if (participant is null)
            return NotFound();

        var transcripts = await _context.Transcripts
            .Where(t => t.ParticipantId == id)
            .Select(t => t.Text)
            .ToListAsync();
        var totalWords = transcripts.Sum(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        var transcriptCount = await _context.Transcripts
            .CountAsync(t => t.ParticipantId == id);

        var meetingCount = await _context.Participants
            .CountAsync(p => p.UserName == participant.UserName);

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

        var allTexts = await _context.Transcripts
            .Where(t => t.RoomId == id)
            .Select(t => t.Text)
            .ToListAsync();
        var totalWords = allTexts.Sum(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        var topicChanges = await _context.TopicChanges
            .CountAsync(tc => tc.RoomId == id);

        var adviceCount = await _context.Advice
            .CountAsync(a => a.RoomId == id);

        var duration = room.EndedAt.HasValue
            ? (int)(room.EndedAt.Value - room.CreatedAt).TotalSeconds
            : 0;

        var speakerData = await _context.Transcripts
            .Where(t => t.RoomId == id)
            .Select(t => new { t.UserName, t.Text })
            .ToListAsync();
        var topSpeakers = speakerData
            .GroupBy(t => t.UserName)
            .Select(g => new SpeakerStatDto
            {
                UserName = g.Key,
                WordCount = g.Sum(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                MessageCount = g.Count()
            })
            .OrderByDescending(s => s.WordCount)
            .Take(5)
            .ToList();

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

    [HttpGet("meeting-efficiency")]
    public async Task<ActionResult<IEnumerable<MeetingEfficiencyDto>>> GetMeetingEfficiency(
        [FromQuery] int days = 30)
    {
        var since = DateTime.UtcNow.AddDays(-days);

        var rooms = await _context.Rooms
            .Where(r => r.CreatedAt >= since && r.Status == RoomStatus.Ended)
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

    [HttpGet("/api/recordings/{id:guid}/debug")]
    public async Task<ActionResult> DebugRecording(Guid id)
    {
        var recording = await _context.MeetingRecordings
            .FirstOrDefaultAsync(r => r.Id == id);

        if (recording is null)
            return NotFound(new { error = "Recording not found" });

        var room = await _context.Rooms.FindAsync(recording.RoomId);

        bool audioFileExists = false;
        long audioFileSize = 0;
        try
        {
            if (!string.IsNullOrEmpty(recording.AudioPath))
            {
                audioFileExists = System.IO.File.Exists(recording.AudioPath);
                if (audioFileExists)
                {
                    var fileInfo = new System.IO.FileInfo(recording.AudioPath);
                    audioFileSize = fileInfo.Length;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check audio file: {Path}", recording.AudioPath);
        }

        return Ok(new
        {
            recording.Id,
            recording.RoomId,
            RoomName = room?.Name ?? "Unknown",
            recording.AudioPath,
            AudioFileExists = audioFileExists,
            AudioFileSizeBytes = audioFileSize,
            HasFullText = !string.IsNullOrEmpty(recording.FullText),
            FullTextLength = recording.FullText?.Length ?? 0,
            FullTextPreview = recording.FullText?.Length > 0
                ? recording.FullText[..Math.Min(recording.FullText.Length, 500)]
                : null,
            HasSummary = !string.IsNullOrEmpty(recording.Summary),
            SummaryLength = recording.Summary?.Length ?? 0,
            SummaryPreview = recording.Summary?.Length > 0
                ? recording.Summary[..Math.Min(recording.Summary.Length, 200)]
                : null,
            recording.Status,
            recording.DurationSeconds,
            recording.FileSizeBytes,
            recording.StartedAt,
            recording.EndedAt,
        });
    }

    [HttpGet("/api/recordings/{id:guid}/export-pdf")]
    public async Task<IActionResult> ExportRecordingPdf(Guid id)
    {
        var recording = await _context.MeetingRecordings
            .FirstOrDefaultAsync(r => r.Id == id);

        if (recording is null)
            return NotFound(new { error = "Recording not found" });

        if (string.IsNullOrEmpty(recording.FullText) && string.IsNullOrEmpty(recording.Summary))
        {
            _logger.LogWarning(
                "Export PDF called for recording {RecordingId} but FullText and Summary are both empty. " +
                "Status: {Status}, FileSizeBytes: {FileSize}",
                id, recording.Status, recording.FileSizeBytes);

            return BadRequest(new
            {
                error = "Cannot export PDF: recording has no transcript data.",
                details = new
                {
                    recording.Status,
                    recording.FileSizeBytes,
                    HasFullText = !string.IsNullOrEmpty(recording.FullText),
                    HasSummary = !string.IsNullOrEmpty(recording.Summary),
                    recording.DurationSeconds,
                    Hint = "The audio file may not have been recorded or post-processing may have failed. " +
                           "Check /api/recordings/{id}/debug for details."
                }
            });
        }

        var room = await _context.Rooms.FindAsync(recording.RoomId);
        var roomName = room?.Name ?? "Unknown Room";

        _logger.LogInformation(
            "Exporting PDF for recording {RecordingId}, room: {RoomName}, " +
            "FullText: {TextLen} chars, Summary: {SummaryLen} chars",
            id, roomName, recording.FullText?.Length ?? 0, recording.Summary?.Length ?? 0);

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
        _logger.LogInformation("PDF generated for recording {RecordingId}: {Size} bytes", id, pdfBytes.Length);
        return File(pdfBytes, "application/pdf", $"meeting_{roomName}_{recording.StartedAt:yyyy-MM-dd}.pdf");
    }

    private void ComposeHeader(IContainer container, string roomName, MeetingRecording recording)
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

    private void ComposeContent(IContainer container, MeetingRecording recording)
    {
        container.Column(col =>
        {
            col.Item().Text("Full Transcript").FontSize(16).Bold();

            if (!string.IsNullOrEmpty(recording.FullText))
            {
                var lines = recording.FullText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    col.Item().PaddingVertical(2).Text(line).FontSize(10);
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
        if (durationSeconds == 0) return 0;

        var durationMinutes = durationSeconds / 60.0;
        var score = 50.0;

        if (durationMinutes >= 20 && durationMinutes <= 90) score += 15;
        else if (durationMinutes < 10) score -= 20;

        score += Math.Min(topicChanges * 3, 15);
        score += Math.Min(adviceCount * 5, 15);

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
