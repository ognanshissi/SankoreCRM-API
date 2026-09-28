namespace Sankore.Modules.Customers.Features.Compliance.Settings.ListCustomerSettings;

using MediatR;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads every M01 tenant setting. A query: it does NOT implement <c>ICommand</c>, so it is
/// neither audited nor wrapped in a transaction.
/// </summary>
public sealed record ListCustomerSettingsQuery
    : IRequest<Result<IReadOnlyList<CustomerSettingDto>>>;
