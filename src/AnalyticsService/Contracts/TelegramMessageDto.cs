using System.ComponentModel.DataAnnotations;
using AnalyticsService.Validation;
using Shared.Contracts.Telegram;

namespace AnalyticsService.Contracts;

public sealed record TelegramMessageDto
{
    [NotDefault]
    public Guid EventId { get; init; }

    [AllowedValues("telegram")]
    public required string Source { get; init; }

    [Required]
    public required string SourceInstance { get; init; }

    [Required]
    public required string CorrelationId { get; init; }

    public long ChatId { get; init; }

    [Range(typeof(long), "1", "9223372036854775807")]
    public long MessageId { get; init; }

    [Required]
    [Range(typeof(long), "1", "9223372036854775807")]
    public long? SenderId { get; init; }

    public required string SenderUsername { get; init; }

    [Required]
    [MaxLength(32000)]
    public required string Text { get; init; }

    public long ThreadId { get; init; }

    [NotDefault]
    public DateTimeOffset OccurredAt { get; init; }

    [Required]
    public string? TeamId { get; init; }

    public static TelegramMessageDto FromEvent(TelegramMessageCreated message, string? teamId) => new()
    {
        EventId = message.EventId,
        Source = message.Source,
        SourceInstance = message.SourceInstance,
        CorrelationId = message.CorrelationId,
        ChatId = message.ChatId,
        MessageId = message.MessageId,
        SenderId = message.SenderId,
        SenderUsername = message.SenderUsername,
        Text = message.Text,
        ThreadId = message.ThreadId ?? 0,
        OccurredAt = message.OccurredAt.ToUniversalTime(),
        TeamId = teamId
    };

    public void Validate()
    {
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(this, new ValidationContext(this), errors, validateAllProperties: true))
            throw new ArgumentException(string.Join("; ", errors.Select(error => error.ErrorMessage)), nameof(TelegramMessageDto));
    }
}
