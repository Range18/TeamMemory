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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<DigestOptions>().BindConfiguration("Digest")
    .Validate(o => TimeZoneInfo.TryFindSystemTimeZoneById(o.TimeZone, out _), "Unknown digest time zone.")
    .Validate(o => DigestFormatter.IsValidTemplate(o.Template), "Digest template must contain all five placeholders.")
    .Validate(o => o.NotificationPollSeconds is >= 1 and <= 60 && o.LegacyTeamChats.Values.All(id => id != 0),
        "Invalid digest notification settings.").ValidateOnStart();
builder.Services.AddScoped<AnalyticsEventInbox>();
builder.Services.AddScoped<GroupDigestService>();
builder.Services.AddHostedService<DigestNotificationWorker>();

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
        endpoint.UseMessageRetry(r =>
        {
            r.Ignore<InvalidDataException>();
            r.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
        }));
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(
            rabbitMq["Host"] ?? "localhost",
            rabbitMq.GetValue<ushort>("Port", 5672),
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
    using var schemaStream = typeof(AppDbContext).Assembly.GetManifestResourceStream("DashboardService.Data.digest-schema.sql")!;
    using var schemaReader = new StreamReader(schemaStream);
    await using var schemaTransaction = await db.Database.BeginTransactionAsync();
    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7421983103)");
    await db.Database.ExecuteSqlRawAsync(await schemaReader.ReadToEndAsync());
    await schemaTransaction.CommitAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();
app.MapHub<GroupsHub>("/hubs/groups").RequireAuthorization();

app.Run();

public partial class Program;
