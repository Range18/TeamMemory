using AnalyticsService.Consumers;
using MassTransit;
using AnalyticsService.Processing;
using Shared.Contracts.Git;
using Shared.Contracts.Telegram;
using AnalyticsService.Analysis;
using AnalyticsService.Infrastructure.Persistence;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

var rabbitMq = builder.Configuration.GetSection("RabbitMq");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddOptions<AnalyticsOptions>().BindConfiguration("Analytics")
    .Validate(o => o.IdleSeconds > 0 && o.MaxWaitSeconds >= o.IdleSeconds && o.MaxMessages is > 0 and <= 100
        && o.PollSeconds is > 0 and <= 10 && o.MaxAttempts > 0 && o.MinimumConfidence is >= 0 and <= 1
        && double.IsFinite(o.RiskMultiplier) && o.RiskMultiplier > 0, "Invalid analytics options.").ValidateOnStart();
builder.Services.AddOptions<LlamaOptions>().BindConfiguration("Llama")
    .Validate(o => o.ContextSize >= 1024 && o.MaxTokens > 0 && o.MaxTokens < o.ContextSize
        && o.TimeoutSeconds > 0 && !string.IsNullOrWhiteSpace(o.ModelPath), "Invalid Llama options.").ValidateOnStart();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Analytics")
    ?? throw new InvalidOperationException("ConnectionStrings:Analytics is required.")));
builder.Services.AddSingleton<AnalyticsStore>();
builder.Services.AddSingleton<IMessageBatchAnalyzer, LlamaMessageBatchAnalyzer>();
builder.Services.AddHostedService<AnalyticsDatabaseInitializer>();

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

        cfg.UseMessageRetry(r =>
        {
            r.Ignore<ArgumentException>();
            r.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        });
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHostedService<OutboxPublisher>();

var host = builder.Build();
host.Run();
