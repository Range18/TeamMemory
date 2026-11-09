using AnalyticsService.Consumers;
using MassTransit;
using AnalyticsService.Processing;
using Shared.Contracts.Git;
using Shared.Contracts.Telegram;

var builder = Host.CreateApplicationBuilder(args);

var rabbitMq = builder.Configuration.GetSection("RabbitMq");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddSingleton<ProcessedEventStore>();
builder.Services.AddScoped(typeof(IntegrationEventFilter<>));

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<TelegramMessageCreatedConsumer>();
    x.AddConsumer<GitPushCommittedConsumer>();
    x.AddConsumer<IntegrationEventConsumer<TelegramMessageEdited>>();
    x.AddConsumer<IntegrationEventConsumer<GitPullRequestOpened>>();
    x.AddConsumer<IntegrationEventConsumer<GitPullRequestClosed>>();
    x.AddConsumer<IntegrationEventConsumer<GitPullRequestMerged>>();
    x.AddConsumer<IntegrationEventConsumer<GitPullRequestReviewed>>();
    x.AddConsumer<IntegrationEventConsumer<GitPullRequestReviewCommentAdded>>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(
            rabbitMq["Host"] ?? "localhost",
            rabbitMq["VirtualHost"] ?? "/",
            h =>
            {
                h.Username(rabbitMq["Username"] ?? "teammemory");
                h.Password(rabbitMq["Password"] ?? "teammemory");
            });

        cfg.UseMessageRetry(r => r.Intervals(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
        cfg.UseConsumeFilter(typeof(IntegrationEventFilter<>), context);
        cfg.ConfigureEndpoints(context);
    });
});

var host = builder.Build();
host.Run();
