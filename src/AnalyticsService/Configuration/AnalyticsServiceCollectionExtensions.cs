using AnalyticsService.Analysis;
using AnalyticsService.Infrastructure.Persistence;
using AnalyticsService.Processing;
using Npgsql;

namespace AnalyticsService.Configuration;

public static class AnalyticsServiceCollectionExtensions
{
    public static IServiceCollection AddAnalytics(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AnalyticsOptions>()
            .BindConfiguration("Analytics")
            .Validate(IsValidAnalyticsOptions, "Invalid analytics options.")
            .ValidateOnStart();

        services.AddOptions<LlamaOptions>()
            .BindConfiguration("Llama")
            .Validate(IsValidLlamaOptions, "Invalid Llama options.")
            .ValidateOnStart();

        services.AddSingleton(_ => NpgsqlDataSource.Create(
            configuration.GetConnectionString("Analytics")
            ?? throw new InvalidOperationException("ConnectionStrings:Analytics is required.")));
        services.AddSingleton<AnalyticsStore>();
        services.AddSingleton<IMessageBatchAnalyzer, LlamaMessageBatchAnalyzer>();
        services.AddHostedService<AnalyticsDatabaseInitializer>();

        return services;
    }

    private static bool IsValidAnalyticsOptions(AnalyticsOptions options) =>
        options.IdleSeconds > 0
        && options.MaxWaitSeconds >= options.IdleSeconds
        && options.MaxMessages is > 0 and <= 100
        && options.PollSeconds is > 0 and <= 10
        && options.MaxAttempts > 0
        && options.MinimumConfidence is >= 0 and <= 1
        && double.IsFinite(options.RiskMultiplier)
        && options.RiskMultiplier > 0;

    private static bool IsValidLlamaOptions(LlamaOptions options) =>
        options.ContextSize >= 1024
        && options.MaxTokens > 0
        && options.MaxTokens < options.ContextSize
        && options.TimeoutSeconds > 0
        && !string.IsNullOrWhiteSpace(options.ModelPath);
}
