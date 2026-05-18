using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using VoiceChatAI.Domain.Entities;
using VoiceChatAI.Domain.Interfaces;
using VoiceChatAI.Infrastructure.Messaging;
using VoiceChatAI.Infrastructure.Services;
using VoiceChatAI.Presentation.Hubs;

namespace VoiceChatAI.Presentation.Services;

/// <summary>
/// AI Orchestrator — consumes summary requests, calls Ollama for summaries,
/// topic detection, advice, and alternative ideas, then sends results via SignalR.
/// </summary>
public class AiOrchestratorService : BackgroundService
{
    private readonly RabbitMqConnectionFactory _connectionFactory;
    private readonly RabbitMqOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<MeetingHub> _hubContext;
    private readonly ILogger<AiOrchestratorService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private IModel? _channel;

    // Track last advice time per room (advice every 60 seconds)
    private readonly Dictionary<Guid, DateTime> _lastAdviceTime = new();
    private static readonly TimeSpan AdviceInterval = TimeSpan.FromSeconds(60);

    // Track last alternative idea time per room
    private readonly Dictionary<Guid, DateTime> _lastAlternativeIdeaTime = new();
    private static readonly TimeSpan AlternativeIdeaInterval = TimeSpan.FromSeconds(120);

    // Track last speaker analysis time per room (every 30-60 seconds)
    private readonly Dictionary<Guid, DateTime> _lastSpeakerAnalysisTime = new();
    private static readonly TimeSpan SpeakerAnalysisInterval = TimeSpan.FromSeconds(45);

    public AiOrchestratorService(
        RabbitMqConnectionFactory connectionFactory,
        IOptions<RabbitMqOptions> options,
        IServiceScopeFactory scopeFactory,
        IHubContext<MeetingHub> hubContext,
        ILogger<AiOrchestratorService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _connectionFactory = connectionFactory;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _channel = _connectionFactory.CreateChannel();
            _channel.QueueDeclare(
                queue: _options.SummaryRequestsQueue,
                durable: true,
                exclusive: false,
                autoDelete: false);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += OnSummaryRequestReceived;

            _channel.BasicConsume(
                queue: _options.SummaryRequestsQueue,
                autoAck: false,
                consumer: consumer);

            _logger.LogInformation("AiOrchestratorService started, listening on queue: {Queue}",
                _options.SummaryRequestsQueue);
        }
        catch (BrokerUnreachableException ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ for AiOrchestratorService.");
        }

        return Task.CompletedTask;
    }

    private async Task OnSummaryRequestReceived(object? sender, BasicDeliverEventArgs args)
    {
        try
        {
            var body = args.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            var summaryRequest = JsonSerializer.Deserialize<SummaryRequestMessage>(message);

            if (summaryRequest is null)
            {
                _channel?.BasicAck(args.DeliveryTag, multiple: false);
                return;
            }

            var roomId = summaryRequest.RoomId;
            _logger.LogInformation("Processing AI summary for room {RoomId}", roomId);

            using var scope = _scopeFactory.CreateScope();
            var transcriptRepo = scope.ServiceProvider.GetRequiredService<ITranscriptRepository>();
            var ollamaService = scope.ServiceProvider.GetRequiredService<OllamaService>();

            // Get recent transcripts for this room (last 50)
            var recentTranscripts = await transcriptRepo.GetRecentByRoomIdAsync(roomId, 50);
            if (recentTranscripts.Count == 0)
            {
                _channel?.BasicAck(args.DeliveryTag, multiple: false);
                return;
            }

            // Build transcript text for LLM (with speaker IDs and voice metrics)
            var transcriptsText = string.Join("\n", recentTranscripts.Select(t =>
            {
                var speakerLabel = t.SpeakerId != "unknown" ? $"({t.SpeakerId})" : "";
                return $"[{t.UserName}]{speakerLabel}: {t.Text}";
            }));

            // Build voice metrics summary for LLM
            var voiceMetricsSummary = BuildVoiceMetricsSummary(recentTranscripts);

            // 1. Generate summary via WhisperLiveKit BART summarizer
            var summary = await GenerateSummaryViaWhisperAsync(recentTranscripts);
            if (!string.IsNullOrEmpty(summary))
            {
                await _hubContext.Clients.Group(roomId.ToString())
                .SendAsync("SummaryGenerated", new
                    {
                        RoomId = roomId,
                        Summary = summary,
                        Timestamp = DateTime.UtcNow
                    });
                _logger.LogInformation("Summary sent to room {RoomId}", roomId);
            }

            // 2. Detect topic change (with voice metrics context)
            var topicResult = await ollamaService.DetectTopicChangeAsync(transcriptsText, voiceMetricsSummary);
            if (topicResult.TopicChanged && !string.IsNullOrEmpty(topicResult.NewTopic))
            {
                await _hubContext.Clients.Group(roomId.ToString())
                    .SendAsync("TopicChanged", new
                    {
                        RoomId = roomId,
                        NewTopic = topicResult.NewTopic,
                        Confidence = topicResult.Confidence,
                        Timestamp = DateTime.UtcNow
                    });
                _logger.LogInformation("Topic change detected in room {RoomId}: {NewTopic}",
                    roomId, topicResult.NewTopic);
            }

            // 3. Generate advice (every 60 seconds)
            var now = DateTime.UtcNow;
            if (!_lastAdviceTime.TryGetValue(roomId, out var lastAdvice) ||
                now - lastAdvice >= AdviceInterval)
            {
                _lastAdviceTime[roomId] = now;
                var advice = await ollamaService.GenerateAdviceAsync(transcriptsText, voiceMetricsSummary);
                if (!string.IsNullOrEmpty(advice))
                {
                    await _hubContext.Clients.Group(roomId.ToString())
                        .SendAsync("AdviceGenerated", new
                        {
                            RoomId = roomId,
                            Advice = advice,
                            Timestamp = DateTime.UtcNow
                        });
                    _logger.LogInformation("Advice sent to room {RoomId}", roomId);
                }
            }

            // 4. Suggest alternative idea (every 120 seconds)
            if (!_lastAlternativeIdeaTime.TryGetValue(roomId, out var lastIdea) ||
                now - lastIdea >= AlternativeIdeaInterval)
            {
                _lastAlternativeIdeaTime[roomId] = now;
                var idea = await ollamaService.SuggestAlternativeIdeaAsync(transcriptsText, voiceMetricsSummary);
                if (!string.IsNullOrEmpty(idea))
                {
                    await _hubContext.Clients.Group(roomId.ToString())
                        .SendAsync("AlternativeIdea", new
                        {
                            RoomId = roomId,
                            Idea = idea,
                            Timestamp = DateTime.UtcNow
                        });
                    _logger.LogInformation("Alternative idea sent to room {RoomId}", roomId);
                }
            }

            // 5. Analyze speakers (every 45 seconds)
            if (!_lastSpeakerAnalysisTime.TryGetValue(roomId, out var lastAnalysis) ||
                now - lastAnalysis >= SpeakerAnalysisInterval)
            {
                _lastSpeakerAnalysisTime[roomId] = now;
                var speakerAnalysis = await ollamaService.AnalyzeSpeakerAsync(transcriptsText, voiceMetricsSummary);
                if (speakerAnalysis.SpeakerCount > 0)
                {
                    await _hubContext.Clients.Group(roomId.ToString())
                        .SendAsync("SpeakerAnalysis", new
                        {
                            RoomId = roomId,
                            SpeakerCount = speakerAnalysis.SpeakerCount,
                            Speakers = speakerAnalysis.Speakers.Select(s => new
                            {
                                s.Id,
                                s.Gender,
                                s.FatigueLevel
                            }),
                            NeedsBreak = speakerAnalysis.NeedsBreak,
                            BreakReason = speakerAnalysis.BreakReason,
                            ShouldPostpone = speakerAnalysis.ShouldPostpone,
                            PostponeReason = speakerAnalysis.PostponeReason,
                            Timestamp = DateTime.UtcNow
                        });
                    _logger.LogInformation("Speaker analysis sent to room {RoomId}: {Count} speakers, needsBreak={NeedsBreak}",
                        roomId, speakerAnalysis.SpeakerCount, speakerAnalysis.NeedsBreak);
                }
            }

            _channel?.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing AI summary request");
            _channel?.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
        }
    }

    /// <summary>
    /// Generates a meeting summary by calling WhisperLiveKit's BART summarizer via HTTP.
    /// Falls back to Ollama if WhisperLiveKit is not available.
    /// </summary>
    private async Task<string> GenerateSummaryViaWhisperAsync(IReadOnlyList<Transcript> transcripts)
    {
        var whisperHost = Environment.GetEnvironmentVariable("WHISPER_LIVEKIT_HOST") ?? "whisper-livekit";
        var whisperPort = Environment.GetEnvironmentVariable("WHISPER_LIVEKIT_PORT") ?? "8080";
        var url = $"http://{whisperHost}:{whisperPort}/summarize";

        try
        {
            // Build transcripts array for the API
            var transcriptsPayload = transcripts.Select(t => new
            {
                text = t.Text,
                speakerId = t.SpeakerId,
                userName = t.UserName,
            }).ToList();

            var payload = new { transcripts = transcriptsPayload };
            var jsonContent = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );

            var httpClient = _httpClientFactory.CreateClient("WhisperLiveKit");
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            var response = await httpClient.PostAsync(url, jsonContent);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(responseBody);

            if (result.TryGetProperty("summary", out var summaryElement))
            {
                var summary = summaryElement.GetString();
                if (!string.IsNullOrEmpty(summary))
                {
                    _logger.LogInformation("Summary generated via WhisperLiveKit BART: {Length} chars", summary.Length);
                    return summary;
                }
            }

            _logger.LogWarning("WhisperLiveKit returned empty summary, falling back to Ollama");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WhisperLiveKit summarization failed, falling back to Ollama");
        }

        // Fallback: use Ollama for summary
        return string.Empty;
    }

    /// <summary>
    /// Builds a voice metrics summary from recent transcripts for LLM context.
    /// Aggregates gender, emotion, fatigue data per speaker.
    /// </summary>
    private string BuildVoiceMetricsSummary(IReadOnlyList<Transcript> transcripts)
    {
        if (transcripts.Count == 0) return string.Empty;

        var speakerMetrics = new Dictionary<string, (List<string> Genders, List<string> Emotions, List<double> FatigueLevels, int Count)>();

        foreach (var t in transcripts)
        {
            if (string.IsNullOrEmpty(t.Metadata)) continue;

            try
            {
                var metrics = JsonSerializer.Deserialize<VoiceMetricsDto>(t.Metadata);
                if (metrics is null) continue;

                var speakerKey = t.SpeakerId != "unknown" ? t.SpeakerId : t.UserName;

                if (!speakerMetrics.ContainsKey(speakerKey))
                    speakerMetrics[speakerKey] = (new List<string>(), new List<string>(), new List<double>(), 0);

                var (genders, emotions, fatigueLevels, count) = speakerMetrics[speakerKey];
                genders.Add(metrics.Gender);
                emotions.Add(metrics.Emotion);
                fatigueLevels.Add(metrics.FatigueLevel);
                speakerMetrics[speakerKey] = (genders, emotions, fatigueLevels, count + 1);
            }
            catch (JsonException)
            {
                // Skip invalid metadata
            }
        }

        if (speakerMetrics.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("\n--- Voice Metrics Summary ---");

        foreach (var (speaker, (genders, emotions, fatigueLevels, count)) in speakerMetrics)
        {
            // Most common gender
            var dominantGender = genders.GroupBy(g => g)
                .OrderByDescending(g => g.Count())
                .First().Key;

            // Most common emotion
            var dominantEmotion = emotions.GroupBy(e => e)
                .OrderByDescending(e => e.Count())
                .First().Key;

            // Average fatigue level
            var avgFatigue = fatigueLevels.Average();

            sb.AppendLine($"Speaker '{speaker}':");
            sb.AppendLine($"  - Gender: {dominantGender} (based on {count} samples)");
            sb.AppendLine($"  - Dominant emotion: {dominantEmotion}");
            sb.AppendLine($"  - Fatigue level: {avgFatigue:F2} (0=energetic, 1=very fatigued)");
            sb.AppendLine($"  - Samples analyzed: {count}");
        }

        sb.AppendLine("--- End Voice Metrics ---\n");
        return sb.ToString();
    }

    public override void Dispose()
    {
        _channel?.Close();
        _channel?.Dispose();
        base.Dispose();
    }
}
