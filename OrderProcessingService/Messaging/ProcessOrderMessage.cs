namespace OrderProcessingService.Messaging;

/// <summary>
/// Message payload published when a new order needs background processing.
/// Kept minimal — the worker re-loads the full order from the database.
/// </summary>
public record ProcessOrderMessage(Guid OrderId);
