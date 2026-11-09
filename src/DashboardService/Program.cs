using MassTransit;
using DashboardService.Consumers;
using Shared.Contracts.Analytics;

var builder = WebApplication.CreateBuilder(args);

var rabbitMq = builder.Configuration.GetSection("RabbitMq");

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<AnalyticsEventConsumer<NewTaskReported>>();
    x.AddConsumer<AnalyticsEventConsumer<BlockerDetected>>();
    x.AddConsumer<AnalyticsEventConsumer<TaskAtRisk>>();
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
        cfg.UseMessageRetry(r => r.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();
app.Run();
