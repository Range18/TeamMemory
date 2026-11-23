using MassTransit;
using Shared.Contracts.Analytics;
using GroupDashboard.Api.Services;

namespace DashboardService.Consumers;

public sealed class AnalyticsEventConsumer<T>(AnalyticsEventInbox inbox, ILogger<AnalyticsEventConsumer<T>> logger) : IConsumer<T>
    where T : TaskAnalyticsEvent
{
    public async Task Consume(ConsumeContext<T> context)
    {
        await inbox.Save(context.Message, context.CancellationToken);
        logger.LogInformation("Received {EventType}, EventId: {EventId}, TeamId: {TeamId}, TaskId: {TaskId}",
            typeof(T).Name, context.Message.EventId, context.Message.TeamId, context.Message.TaskId);
    }
}
