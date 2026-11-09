using MassTransit;
using Shared.Contracts;

namespace AnalyticsService.Consumers;

public sealed class IntegrationEventConsumer<T>(ILogger<IntegrationEventConsumer<T>> logger)
    : IConsumer<T> where T : IntegrationEventBase
{
    public Task Consume(ConsumeContext<T> context)
    {
        // Task tracking and LLM analysis can be invoked here. Do not swallow failures:
        // MassTransit acknowledges the delivery only after this task succeeds.
        logger.LogInformation("Received {EventType} from {Source}", typeof(T).Name, context.Message.Source);
        return Task.CompletedTask;
    }
}
