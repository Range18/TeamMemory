using AnalyticsService.Configuration;
using AnalyticsService.Processing;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddAnalytics(builder.Configuration);
builder.Services.AddAnalyticsMessaging(builder.Configuration);
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHostedService<OutboxPublisher>();

var host = builder.Build();
host.Run();
