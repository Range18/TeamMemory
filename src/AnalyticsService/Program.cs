using AnalyticsService.Configuration;
using AnalyticsService.Processing;
using DotNetEnv;

Env.NoClobber().TraversePath().Load(Path.Combine(AppContext.BaseDirectory, ".env"));

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddAnalytics(builder.Configuration);
builder.Services.AddAnalyticsMessaging(builder.Configuration);
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHostedService<OutboxPublisher>();

var host = builder.Build();
host.Run();
