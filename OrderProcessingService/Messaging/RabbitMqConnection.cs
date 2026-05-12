using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace OrderProcessingService.Messaging;

/// <summary>
/// Singleton wrapper around a RabbitMQ <see cref="IConnection"/>. The connection is
/// established lazily on first use and reused for the lifetime of the application.
/// </summary>
public sealed class RabbitMqConnection : IDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnection> _logger;
    private readonly object _lock = new();
    private IConnection? _connection;

    public RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string QueueName => _options.Queue;

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
                UserName = _options.User,
                Password = _options.Password,
                DispatchConsumersAsync = true,
                // Dispatch up to N consumer events concurrently per consumer.
                // Without this the async dispatcher uses a single worker and processes
                // messages sequentially even if prefetchCount > 1.
                ConsumerDispatchConcurrency = 5,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
            };

            _logger.LogInformation("Connecting to RabbitMQ at {Host}:{Port}", _options.Host, _options.Port);
            _connection = factory.CreateConnection();
            return _connection;
        }
    }

    /// <summary>Creates a channel and declares the orders queue as durable.</summary>
    public IModel CreateChannelAndDeclareQueue()
    {
        var channel = GetConnection().CreateModel();
        channel.QueueDeclare(
            queue: _options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);
        return channel;
    }

    public void Dispose()
    {
        try { _connection?.Dispose(); }
        catch { /* swallow on shutdown */ }
    }
}
