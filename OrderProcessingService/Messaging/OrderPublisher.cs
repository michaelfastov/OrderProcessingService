using System.Text.Json;
using RabbitMQ.Client;

namespace OrderProcessingService.Messaging;

public interface IOrderPublisher
{
    void Publish(ProcessOrderMessage message);
}

public sealed class OrderPublisher : IOrderPublisher
{
    private readonly RabbitMqConnection _connection;
    private readonly ILogger<OrderPublisher> _logger;

    public OrderPublisher(RabbitMqConnection connection, ILogger<OrderPublisher> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public void Publish(ProcessOrderMessage message)
    {
        using var channel = _connection.CreateChannelAndDeclareQueue();
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
}
