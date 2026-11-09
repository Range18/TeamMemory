using MassTransit;
using Shared.Contracts.Analytics;

namespace DashboardService.Consumers;

// Delivery endpoint for the initial scenario. Digest storage/UI are a separate feature.
public sealed class AnalyticsEventConsumer<T>(ILogger<AnalyticsEventConsumer<T>> logger) : IConsumer<T>
    where T : TaskAnalyticsEvent
{
    public Task Consume(ConsumeContext<T> context)
    {
        logger.LogInformation("Received {EventType}, EventId: {EventId}, TeamId: {TeamId}, TaskId: {TaskId}",
            typeof(T).Name, context.Message.EventId, context.Message.TeamId, context.Message.TaskId);
        return Task.CompletedTask;
    }
}
