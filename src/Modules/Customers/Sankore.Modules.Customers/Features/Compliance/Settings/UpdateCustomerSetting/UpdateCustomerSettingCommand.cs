namespace Sankore.Modules.Customers.Features.Compliance.Settings.UpdateCustomerSetting;

using MediatR;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Writes one M01 tenant setting.
/// <para>
/// A mutation, therefore <see cref="ICommand"/> (transaction + audit) and
/// <see cref="IResourceCommand"/>: changing <c>retention-years</c> or
/// <c>duplicate-score-threshold</c> shifts the behaviour of the whole module for the whole
/// tenant, so the audit trail must name the key that moved and the value it moved to.
/// </para>
/// <para>
/// Nothing here is personal data — the values are thresholds and formats — so no property is
/// marked <c>[SensitiveData]</c>: the audit entry deliberately keeps the value in clear.
/// </para>
/// </summary>
public sealed record UpdateCustomerSettingCommand(string Key, string Value)
    : IRequest<Result<CustomerSettingDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "CustomerSetting";
    public string? ResourceId => Key;
}
