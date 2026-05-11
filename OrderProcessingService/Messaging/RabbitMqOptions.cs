namespace OrderProcessingService.Messaging;

public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public required string Host { get; set; }
    public required int Port { get; set; }
    public required string User { get; set; }
    public required string Password { get; set; }
    public required string Queue { get; set; }
}
