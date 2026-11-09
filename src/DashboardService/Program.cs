using GroupDashboard.Api.Auth;
using GroupDashboard.Api.Data;
using GroupDashboard.Api.Hubs;
using GroupDashboard.Api.Messaging;
using GroupDashboard.Api.Services;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using DashboardService.Consumers;
using Shared.Contracts.Analytics;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton<TelegramInitDataValidator>();
builder.Services.AddSingleton<TelegramChatRequestService>();
builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services
    .AddAuthentication("TelegramMiniApp")
    .AddScheme<AuthenticationSchemeOptions, TelegramMiniAppAuthenticationHandler>(
        "TelegramMiniApp", _ => { });
builder.Services.AddAuthorization();

var rabbitMq = builder.Configuration.GetSection("RabbitMq");
var groupUpdatesQueue = rabbitMq["QueueName"] ?? "groups.updated.v1";

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<GroupUpdatesConsumer>().ExcludeFromConfigureEndpoints();
    x.AddConsumer<AnalyticsEventConsumer<NewTaskReported>>();
    x.AddConsumer<AnalyticsEventConsumer<BlockerDetected>>();
    x.AddConsumer<AnalyticsEventConsumer<TaskAtRisk>>();
    x.AddConfigureEndpointsCallback((name, endpoint) =>
        endpoint.UseMessageRetry(r => r.Intervals(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15))));
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

        cfg.ReceiveEndpoint(groupUpdatesQueue, endpoint =>
        {
            endpoint.UseRawJsonDeserializer(RawSerializerOptions.All, isDefault: true);
            endpoint.UseMessageRetry(retry =>
            {
                retry.Intervals(1000, 5000, 10000);
                retry.Ignore<InvalidDataException>();
            });
            endpoint.ConfigureConsumer<GroupUpdatesConsumer>(context);
        });
        cfg.ConfigureEndpoints(context);
    });
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("MiniApp", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();

app.UseCors("MiniApp");
app.UseAuthentication();
app.UseAuthorization();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS group_managers (
            chat_id bigint NOT NULL REFERENCES groups(chat_id) ON DELETE CASCADE,
            manager_telegram_id bigint NOT NULL,
            connected_at_utc timestamp with time zone NOT NULL,
            CONSTRAINT pk_group_managers PRIMARY KEY (chat_id, manager_telegram_id)
        );
        CREATE INDEX IF NOT EXISTS ix_group_managers_manager_telegram_id
            ON group_managers (manager_telegram_id);
        """);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();
app.MapHub<GroupsHub>("/hubs/groups").RequireAuthorization();

app.Run();
