using MassTransit;
using Shared.Contracts.Telegram;
using AnalyticsService.Infrastructure.Persistence;

namespace AnalyticsService.Consumers;

public sealed class TelegramMessageCreatedConsumer(AnalyticsStore store) : IConsumer<TelegramMessageCreated>
{
    public Task Consume(ConsumeContext<TelegramMessageCreated> context) =>
        store.Accept(context.Message, context.CancellationToken);
}
