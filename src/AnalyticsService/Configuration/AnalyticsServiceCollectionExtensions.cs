using AnalyticsService.Analysis;
using AnalyticsService.Infrastructure.Persistence;
using AnalyticsService.Processing;
using Microsoft.EntityFrameworkCore;

namespace AnalyticsService.Configuration;

public static class AnalyticsServiceCollectionExtensions
{
    public static IServiceCollection AddAnalytics(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AnalyticsOptions>()
            .BindConfiguration("Analytics")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<LlmApiOptions>()
            .BindConfiguration("LlmApi")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContextFactory<AnalyticsDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("Analytics")
            ?? throw new InvalidOperationException("ConnectionStrings:Analytics is required.")));
        services.AddSingleton<AnalyticsStore>();
        services.AddHttpClient(HttpMessageBatchAnalyzer.HttpClientName,
            client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IMessageBatchAnalyzer, HttpMessageBatchAnalyzer>();
        services.AddHostedService<AnalyticsDatabaseInitializer>();

        return services;
    }
}
