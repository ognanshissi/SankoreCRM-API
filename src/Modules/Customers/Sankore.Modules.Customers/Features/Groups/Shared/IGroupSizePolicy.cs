namespace Sankore.Modules.Customers.Features.Groups.Shared;

using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;

/// <summary>
/// Resolves the tenant-configured size envelope of a group from
/// <see cref="ICustomerSettings"/>. Extracted from the handlers so that the
/// mapping "group type → setting key" lives in exactly one place: adding a
/// fourth <see cref="GroupType"/> must fail to compile here rather than silently
/// fall back to a wrong default in five different handlers.
/// </summary>
internal interface IGroupSizePolicy
{
    /// <summary>Minimum number of active members below which the group is flagged (never blocked).</summary>
    Task<int> MinSizeAsync(Guid tenantId, GroupType type, CancellationToken ct);

    /// <summary>Maximum number of active members; reaching it blocks any further join.</summary>
    Task<int> MaxSizeAsync(Guid tenantId, GroupType type, CancellationToken ct);

    /// <summary>
    /// True when a client may belong to at most one active solidarity group
    /// (tenant setting <c>solidarity-single-group-rule</c>).
    /// </summary>
    Task<bool> SolidaritySingleGroupRuleAsync(Guid tenantId, CancellationToken ct);
}

internal sealed class GroupSizePolicy(ICustomerSettings settings) : IGroupSizePolicy
{
    public Task<int> MinSizeAsync(Guid tenantId, GroupType type, CancellationToken ct)
        => settings.GetIntAsync(tenantId, MinSizeKey(type), ct);

    public Task<int> MaxSizeAsync(Guid tenantId, GroupType type, CancellationToken ct)
        => settings.GetIntAsync(tenantId, MaxSizeKey(type), ct);

    public Task<bool> SolidaritySingleGroupRuleAsync(Guid tenantId, CancellationToken ct)
        => settings.GetBoolAsync(tenantId, CustomerSettingKeys.SolidaritySingleGroupRule, ct);

    internal static string MinSizeKey(GroupType type) => type switch
    {
        GroupType.SolidarityGroup => CustomerSettingKeys.GroupMinSizeSolidarity,
        GroupType.Tontine => CustomerSettingKeys.GroupMinSizeTontine,
        GroupType.Vsla => CustomerSettingKeys.GroupMinSizeVsla,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown group type."),
    };

    internal static string MaxSizeKey(GroupType type) => type switch
    {
        GroupType.SolidarityGroup => CustomerSettingKeys.GroupMaxSizeSolidarity,
        GroupType.Tontine => CustomerSettingKeys.GroupMaxSizeTontine,
        GroupType.Vsla => CustomerSettingKeys.GroupMaxSizeVsla,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown group type."),
    };
}
