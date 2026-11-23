using System.ComponentModel.DataAnnotations;

namespace AnalyticsService.Analysis;

public sealed class LlmApiOptions : IValidatableObject
{
    [Required]
    public string Endpoint { get; set; } = "http://localhost:11434/v1/chat/completions";

    [Required]
    public string Model { get; set; } = "qwen2.5:7b";

    public string? ApiKey { get; set; }

    [Range(1, int.MaxValue)]
    public int MaxTokens { get; set; } = 1500;

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 300;

    public bool UseJsonMode { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
            yield return new ValidationResult("Endpoint must be an absolute HTTP or HTTPS URL.", [nameof(Endpoint)]);
    }
}
