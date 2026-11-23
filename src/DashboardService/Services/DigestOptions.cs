namespace GroupDashboard.Api.Services;

public sealed class DigestOptions
{
    public string TimeZone { get; set; } = "Asia/Yekaterinburg";
    public int NotificationPollSeconds { get; set; } = 2;
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
