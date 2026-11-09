using MassTransit;
using Shared.Contracts;

namespace AnalyticsService.Processing;

public sealed class IntegrationEventFilter<T>(ProcessedEventStore store,
    ILogger<IntegrationEventFilter<T>> logger) : IFilter<ConsumeContext<T>> where T : class
{
    public void Probe(ProbeContext context) => context.CreateFilterScope("integration-event");

    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        if (context.Message is not IntegrationEventBase integrationEvent)
        {
            await next.Send(context);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["EventId"] = integrationEvent.EventId,
            ["MessageId"] = context.MessageId,
            ["CorrelationId"] = integrationEvent.CorrelationId,
            ["Source"] = integrationEvent.Source
        });

        try
        {
            var processed = await store.Process(integrationEvent.EventId, context.MessageId,
                () => next.Send(context), context.CancellationToken);
            if (!processed)
                logger.LogInformation("Skipped duplicate {EventType}", typeof(T).Name);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to process {EventType}", typeof(T).Name);
            throw;
        }
    }
}
