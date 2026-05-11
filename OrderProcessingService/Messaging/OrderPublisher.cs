using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace OrderProcessingService.Messaging;

public interface IOrderPublisher
{
    void Publish(ProcessOrderMessage message);
}

public sealed class OrderPublisher : IOrderPublisher, IDisposable
{
    private readonly RabbitMqConnection _connection;
    private readonly ILogger<OrderPublisher> _logger;
    private readonly Lazy<IModel> _channel;

    public OrderPublisher(RabbitMqConnection connection, ILogger<OrderPublisher> logger)
    {
        _connection = connection;
        _logger = logger;
        _channel = new Lazy<IModel>(() => _connection.CreateChannelAndDeclareQueue());
    }

    public void Publish(ProcessOrderMessage message)
    {
        var channel = _channel.Value;
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";

        channel.BasicPublish(
            exchange: string.Empty,
            routingKey: _connection.QueueName,
            mandatory: false,
            basicProperties: properties,
            body: body);

        _logger.LogInformation("Published order {OrderId} to queue {Queue}", message.OrderId, _connection.QueueName);
    }

    public void Dispose()
    {
        if (_channel.IsValueCreated)
        {
            try { _channel.Value.Dispose(); }
            catch { /* swallow on shutdown */ }
        }
    }
}
