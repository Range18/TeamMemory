namespace GroupDashboard.Api.Services;

public sealed class DigestOptions
{
    public string TimeZone { get; set; } = "Asia/Yekaterinburg";
    public int NotificationPollSeconds { get; set; } = 2;
    // Explicit routing for old events which have TeamId but no ChatId. No inferred routing.
    public Dictionary<string, long> LegacyTeamChats { get; set; } = [];
    public string Template { get; set; } = """
        Дайджест: {{group_title}}
        Дата: {{date}}

        Новые задачи:
        {{new_tasks}}

        Блокеры:
        {{blockers}}

        Риски:
        {{risks}}
        """;
}
