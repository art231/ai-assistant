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
using VoiceChatAI.Presentation.Hubs;

namespace VoiceChatAI.Presentation.Services;

/// <summary>
/// Consumes transcribed text from WhisperLiveKit, saves to PostgreSQL,
/// and triggers AI summary generation every 30 seconds.
/// </summary>
public class TranscriptConsumer : BackgroundService
{
    private readonly RabbitMqConnectionFactory _connectionFactory;
    private readonly RabbitMqOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<MeetingHub> _hubContext;
    private readonly ILogger<TranscriptConsumer> _logger;
    private IModel? _channel;
    private DateTime _lastSummaryTime = DateTime.MinValue;
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(30);

    public TranscriptConsumer(
        RabbitMqConnectionFactory connectionFactory,
        IOptions<RabbitMqOptions> options,
        IServiceScopeFactory scopeFactory,
        IHubContext<MeetingHub> hubContext,
        ILogger<TranscriptConsumer> logger)
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
            
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += OnMessageReceived;

            _channel.BasicConsume(
                queue: _options.TranscriptsQueue,
                autoAck: false,
                consumer: consumer);

            _logger.LogInformation("TranscriptConsumer started, listening on queue: {Queue}", _options.TranscriptsQueue);
        }
        catch (BrokerUnreachableException ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ.");
        }

        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(object? sender, BasicDeliverEventArgs args)
    {
        try
        {
            var body = args.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);

            var transcriptMessage = JsonSerializer.Deserialize<TranscriptMessage>(message);
            if (transcriptMessage is null)
            {
                _logger.LogWarning("Received null transcript message");
                _channel?.BasicAck(args.DeliveryTag, multiple: false);
                return;
            }

            // Save transcript to database
            using var scope = _scopeFactory.CreateScope();
            var transcriptRepo = scope.ServiceProvider.GetRequiredService<ITranscriptRepository>();

            var transcript = new Transcript(
                transcriptMessage.RoomId,
                transcriptMessage.UserName,
                transcriptMessage.Text,
                transcriptMessage.ParticipantId,
                transcriptMessage.IsFinal,
                transcriptMessage.Language);

            await transcriptRepo.CreateAsync(transcript);

            // Send transcript to room via SignalR
            await _hubContext.Clients.Group(transcriptMessage.RoomId.ToString())
                .SendAsync("TranscriptReceived", new
                {
                    RoomId = transcriptMessage.RoomId,
                    ParticipantId = transcriptMessage.ParticipantId,
                    UserName = transcriptMessage.UserName,
                    Text = transcriptMessage.Text,
                    IsFinal = transcriptMessage.IsFinal,
                    Language = transcriptMessage.Language,
                    Timestamp = transcriptMessage.Timestamp
                });

            _logger.LogDebug("Transcript saved: [{User}] {Text}", transcriptMessage.UserName, 
                transcriptMessage.Text[..Math.Min(50, transcriptMessage.Text.Length)]);

            // Check if we need to generate a summary (every 30 seconds)
            if (DateTime.UtcNow - _lastSummaryTime >= SummaryInterval)
            {
                _lastSummaryTime = DateTime.UtcNow;
                await PublishSummaryRequest(transcriptMessage.RoomId);
            }

            _channel?.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing transcript");
            _channel?.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
        }
    }

    private async Task PublishSummaryRequest(Guid roomId)
    {
        try
        {
            using var channel = _connectionFactory.CreateChannel();
            channel.QueueDeclare(
                queue: _options.SummaryRequestsQueue,
                durable: true,
                exclusive: false,
                autoDelete: false);
            var summaryRequest = new SummaryRequestMessage
            {
                RoomId = roomId,
                RequestedAt = DateTime.UtcNow
            };

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(summaryRequest));
            channel.BasicPublish(
                exchange: string.Empty,
                routingKey: _options.SummaryRequestsQueue,
                basicProperties: null,
                body: body);

            _logger.LogInformation("Summary request published for room {RoomId}", roomId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish summary request for room {RoomId}", roomId);
        }
    }

    public override void Dispose()
    {
        _channel?.Close();
        _channel?.Dispose();
        base.Dispose();
    }
}
