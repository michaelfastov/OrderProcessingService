using System.Text;
using System.Text.Json;
using OrderProcessingService.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderProcessingService.Processing;

/// <summary>
/// Long-running background service that consumes <see cref="ProcessOrderMessage"/>
/// from RabbitMQ and delegates to <see cref="OrderProcessor"/> in a scoped DI context.
/// </summary>
public class OrderConsumer : BackgroundService
{
    private readonly RabbitMqConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderConsumer> _logger;
    private IModel? _channel;

    public OrderConsumer(
        RabbitMqConnection connection,
        IServiceScopeFactory scopeFactory,
        ILogger<OrderConsumer> logger)
    {
        _connection = connection;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _channel = _connection.CreateChannelAndDeclareQueue();
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 5, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnMessageReceived;

        _channel.BasicConsume(queue: _connection.QueueName, autoAck: false, consumer: consumer);
        _logger.LogInformation("OrderConsumer started, listening on queue {Queue}", _connection.QueueName);

        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(object sender, BasicDeliverEventArgs ea)
    {
        var body = ea.Body.ToArray();
        ProcessOrderMessage? message = null;

        try
        {
            message = JsonSerializer.Deserialize<ProcessOrderMessage>(body);
            if (message is null)
                throw new InvalidOperationException("Empty or invalid message payload");

            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<OrderProcessor>();
            await processor.ProcessAsync(message.OrderId, CancellationToken.None);

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed handling message {OrderId} (delivery {DeliveryTag}); dropping (not requeued)",
                message?.OrderId, ea.DeliveryTag);

            // Reject without requeue — failure is already persisted on the order row.
            // In a real system this would route to a DLQ.
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try { _channel?.Close(); } catch { /* ignore */ }
        try { _channel?.Dispose(); } catch { /* ignore */ }
        return base.StopAsync(cancellationToken);
    }
}
