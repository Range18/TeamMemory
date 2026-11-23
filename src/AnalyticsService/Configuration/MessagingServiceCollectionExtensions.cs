using AnalyticsService.Consumers;
using MassTransit;
using Shared.Contracts.Git;
using Shared.Contracts.Telegram;

namespace AnalyticsService.Configuration;

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var rabbitMq = configuration.GetSection("RabbitMq");

        services.AddMassTransit(registration =>
        {
            registration.AddConsumer<TelegramMessageCreatedConsumer>();
            registration.AddConsumer<GitPushCommittedConsumer>();
            registration.AddConsumer<IntegrationEventConsumer<TelegramMessageEdited>>();
            registration.AddConsumer<IntegrationEventConsumer<GitPullRequestOpened>>();
            registration.AddConsumer<IntegrationEventConsumer<GitPullRequestClosed>>();
            registration.AddConsumer<IntegrationEventConsumer<GitPullRequestMerged>>();
            registration.AddConsumer<IntegrationEventConsumer<GitPullRequestReviewed>>();
            registration.AddConsumer<IntegrationEventConsumer<GitPullRequestReviewCommentAdded>>();

            registration.UsingRabbitMq((context, bus) =>
            {
                bus.Host(
                    rabbitMq["Host"] ?? "localhost",
                    rabbitMq["VirtualHost"] ?? "/",
                    host =>
                    {
                        host.Username(rabbitMq["Username"] ?? "teammemory");
                        host.Password(rabbitMq["Password"] ?? "teammemory");
                    });

                bus.UseMessageRetry(retry =>
                {
                    retry.Ignore<ArgumentException>();
                    retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
                });

                bus.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
