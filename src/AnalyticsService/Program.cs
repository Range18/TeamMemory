using AnalyticsService.Configuration;
using AnalyticsService.Processing;
using Shared.Contracts.Git;
using Shared.Contracts.Telegram;
using AnalyticsService.Analysis;
using AnalyticsService.Infrastructure.Persistence;
using Npgsql;
using DotNetEnv;

Env.NoClobber().TraversePath().Load(Path.Combine(AppContext.BaseDirectory, ".env"));

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));
builder.Services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddAnalytics(builder.Configuration);
builder.Services.AddAnalyticsMessaging(builder.Configuration);
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHostedService<OutboxPublisher>();

var host = builder.Build();
var readinessPath = builder.Configuration["Readiness:FilePath"];
if (!string.IsNullOrWhiteSpace(readinessPath))
{
    File.Delete(readinessPath);
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStarted.Register(() => File.WriteAllText(readinessPath, "ready"));
    lifetime.ApplicationStopping.Register(() => File.Delete(readinessPath));
}
host.Run();
