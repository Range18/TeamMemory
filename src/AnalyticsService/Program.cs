using AnalyticsService.Consumers;
using MassTransit;

var builder = Host.CreateApplicationBuilder(args);

var rabbitMq = builder.Configuration.GetSection("RabbitMq");

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<TelegramMessageCreatedConsumer>();
    x.AddConsumer<GitPushCommittedConsumer>();

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

        cfg.ConfigureEndpoints(context);
    });
});

var host = builder.Build();
host.Run();
