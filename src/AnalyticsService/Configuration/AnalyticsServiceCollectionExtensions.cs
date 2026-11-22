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

        services.AddOptions<LlamaOptions>()
            .BindConfiguration("Llama")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContextFactory<AnalyticsDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString("Analytics")
            ?? throw new InvalidOperationException("ConnectionStrings:Analytics is required.")));
        services.AddSingleton<AnalyticsStore>();
        services.AddSingleton<IMessageBatchAnalyzer, LlamaMessageBatchAnalyzer>();
        services.AddHostedService<AnalyticsDatabaseInitializer>();

        return services;
    }
}
