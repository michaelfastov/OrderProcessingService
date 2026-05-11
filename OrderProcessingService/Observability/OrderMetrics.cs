using Prometheus;

namespace OrderProcessingService.Observability;

/// <summary>
/// Static counters scraped by Prometheus at <c>/metrics</c>.
/// </summary>
public static class OrderMetrics
{
    public static readonly Counter ProcessedOrders = Metrics.CreateCounter(
        "orders_processed_total",
        "Total number of orders successfully processed by the background worker.");

    public static readonly Counter FailedOrders = Metrics.CreateCounter(
        "orders_failed_total",
        "Total number of orders that failed during background processing.");

    public static readonly Counter ReceivedOrders = Metrics.CreateCounter(
        "orders_received_total",
        "Total number of orders accepted via the HTTP API.");
}
