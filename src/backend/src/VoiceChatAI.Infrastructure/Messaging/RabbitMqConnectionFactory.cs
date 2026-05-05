using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace VoiceChatAI.Infrastructure.Messaging;

public class RabbitMqConnectionFactory : IDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnectionFactory> _logger;
    private IConnection? _connection;
    private readonly object _lock = new();
    private bool _disposed;

    public RabbitMqConnectionFactory(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnectionFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IConnection GetConnection()
    {
        if (_connection is { IsOpen: true })
            return _connection;

        lock (_lock)
        {
            if (_connection is { IsOpen: true })
                return _connection;

            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                DispatchConsumersAsync = true,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
            };

            _connection = factory.CreateConnection("VoiceChatAI");
            _logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}", _options.Host, _options.Port);
            return _connection;
        }
    }

    public IModel CreateChannel()
    {
        var connection = GetConnection();
        var channel = connection.CreateModel();
        
        // Declare queues
        channel.QueueDeclare(_options.AudioChunksQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueDeclare(_options.TranscriptsQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueDeclare(_options.SummaryRequestsQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueDeclare(_options.AdviceRequestsQueue, durable: true, exclusive: false, autoDelete: false);

        return channel;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _connection?.Close();
        _connection?.Dispose();
        _disposed = true;
    }
}
