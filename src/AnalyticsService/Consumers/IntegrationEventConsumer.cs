using MassTransit;
using Shared.Contracts;

namespace AnalyticsService.Consumers;

public sealed class IntegrationEventConsumer<T>(ILogger<IntegrationEventConsumer<T>> logger)
    : IConsumer<T> where T : IntegrationEventBase
{
    public Task Consume(ConsumeContext<T> context)
    {
        // Edits and Git processing are outside the initial Telegram analysis scenario.
        logger.LogInformation("Received {EventType} from {Source}", typeof(T).Name, context.Message.Source);
        return Task.CompletedTask;
    }
}
