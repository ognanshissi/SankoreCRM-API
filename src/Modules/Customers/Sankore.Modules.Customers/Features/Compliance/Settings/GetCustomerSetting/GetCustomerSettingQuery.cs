namespace Sankore.Modules.Customers.Features.Compliance.Settings.GetCustomerSetting;

using MediatR;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads a single M01 tenant setting by key. A query: no <c>ICommand</c>, no audit entry.
/// An unrecognised key answers <c>SETTING_UNKNOWN</c> rather than an empty payload, so a typo
/// in a client-side integration fails loudly instead of silently resolving to nothing.
/// </summary>
public sealed record GetCustomerSettingQuery(string Key)
    : IRequest<Result<CustomerSettingDto>>;
