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
    private IModel? _channel;

    // Track last advice time per room (advice every 60 seconds)
    private readonly Dictionary<Guid, DateTime> _lastAdviceTime = new();
    private static readonly TimeSpan AdviceInterval = TimeSpan.FromSeconds(60);

    // Track last alternative idea time per room
    private readonly Dictionary<Guid, DateTime> _lastAlternativeIdeaTime = new();
    private static readonly TimeSpan AlternativeIdeaInterval = TimeSpan.FromSeconds(120);

    public AiOrchestratorService(
        RabbitMqConnectionFactory connectionFactory,
        IOptions<RabbitMqOptions> options,
        IServiceScopeFactory scopeFactory,
        IHubContext<MeetingHub> hubContext,
        ILogger<AiOrchestratorService> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
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

            // Build transcript text for LLM
            var transcriptsText = string.Join("\n", recentTranscripts.Select(t =>
                $"[{t.UserName}]: {t.Text}"));

            // 1. Generate summary
            var summary = await ollamaService.GenerateSummaryAsync(transcriptsText);
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

            // 2. Detect topic change
            var topicResult = await ollamaService.DetectTopicChangeAsync(transcriptsText);
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
                var advice = await ollamaService.GenerateAdviceAsync(transcriptsText);
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
                var idea = await ollamaService.SuggestAlternativeIdeaAsync(transcriptsText);
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

            _channel?.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing AI summary request");
            _channel?.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
        }
    }

    public override void Dispose()
    {
        _channel?.Close();
        _channel?.Dispose();
        base.Dispose();
    }
}
