namespace GroupDashboard.Api.Auth;

public sealed class GroupAccessPolicy(IHostEnvironment environment)
{
    public const long DevelopmentManagerId = 123456789;

    public bool CanViewAllGroups(long managerId) =>
        environment.IsDevelopment() && managerId == DevelopmentManagerId;

    public IEnumerable<long> NotificationRecipients(IEnumerable<long> managers) =>
        CanViewAllGroups(DevelopmentManagerId)
            ? managers.Append(DevelopmentManagerId).Distinct()
            : managers.Distinct();
}
