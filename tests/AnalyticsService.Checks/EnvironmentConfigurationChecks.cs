using AnalyticsService.Analysis;
using DotNetEnv;
using Microsoft.Extensions.Configuration;

internal static class EnvironmentConfigurationChecks
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"teammemory-env-check-{Guid.NewGuid():N}");
        var nestedDirectory = Path.Combine(directory, "bin");
        var path = Path.Combine(directory, ".env");
        var prefix = $"TEAMMEMORY_CHECK_{Guid.NewGuid():N}__";
        var keys = new[] { "LlmApi__ApiKey", "LlmApi__Model", "Analytics__ChatTeams__-100" };
        Directory.CreateDirectory(nestedDirectory);
        try
        {
            File.WriteAllLines(path,
            [
                $"{prefix}LlmApi__ApiKey=test-key-from-file",
                $"{prefix}LlmApi__Model=model-from-file",
                $"{prefix}Analytics__ChatTeams__-100=team-a"
            ]);
            Environment.SetEnvironmentVariable(prefix + "LlmApi__Model", "model-from-process");
            Env.NoClobber().TraversePath().Load(Path.Combine(nestedDirectory, ".env"));
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var llm = configuration.GetSection("LlmApi").Get<LlmApiOptions>()!;
            var analytics = configuration.GetSection("Analytics").Get<AnalyticsOptions>()!;
            if (llm.ApiKey != "test-key-from-file" || llm.Model != "model-from-process")
                throw new Exception("The .env file was not loaded or process environment precedence was lost.");
            if (analytics.ChatTeams.GetValueOrDefault("-100") != "team-a")
                throw new Exception("Negative Telegram chat ID was not bound from .env.");
        }
        finally
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(prefix + key, null);
            File.Delete(path);
            Directory.Delete(nestedDirectory);
            Directory.Delete(directory);
        }
        Console.WriteLine("PASS: parent .env discovery, process environment precedence and nested option binding");
    }
}
