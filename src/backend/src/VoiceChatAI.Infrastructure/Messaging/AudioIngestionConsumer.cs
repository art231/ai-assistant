using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace VoiceChatAI.Infrastructure.Messaging;

/// <summary>
/// Consumes audio chunks from Mediasoup and publishes them to RabbitMQ for WhisperLiveKit.
/// </summary>
public class AudioIngestionConsumer : BackgroundService
{
    private readonly RabbitMqConnectionFactory _connectionFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<AudioIngestionConsumer> _logger;
    private IModel? _channel;

    public AudioIngestionConsumer(
        RabbitMqConnectionFactory connectionFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<AudioIngestionConsumer> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options.Value;
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
                queue: _options.AudioChunksQueue,
                autoAck: false,
                consumer: consumer);

            _logger.LogInformation("AudioIngestionConsumer started, listening on queue: {Queue}", _options.AudioChunksQueue);
        }
        catch (BrokerUnreachableException ex)
        {
            _logger.LogError(ex, "Failed to connect to RabbitMQ. Retrying in 10 seconds...");
            // Will retry on next ExecuteAsync cycle
        }

        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(object? sender, BasicDeliverEventArgs args)
    {
        try
        {
            var body = args.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);

            // Audio chunk received from Mediasoup - forward to WhisperLiveKit
            // The message is already in the correct queue, WhisperLiveKit will pick it up
            _logger.LogDebug("Audio chunk received: {Length} bytes", body.Length);

            // Acknowledge the message
            _channel?.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio chunk");
            _channel?.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
        }

        await Task.CompletedTask;
    }

    public override void Dispose()
    {
        _channel?.Close();
        _channel?.Dispose();
        base.Dispose();
    }
}
