namespace Sankore.Modules.Customers.Features.Compliance.Settings.ListCustomerSettings;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListCustomerSettingsHandler(CustomersDbContext db, ITenantModules modules)
    : IRequestHandler<ListCustomerSettingsQuery, Result<IReadOnlyList<CustomerSettingDto>>>
{
    public async Task<Result<IReadOnlyList<CustomerSettingDto>>> Handle(
        ListCustomerSettingsQuery query, CancellationToken ct)
    {
        // The tenant query filter on CustomerSetting does the isolation: this read is
        // already scoped to the JWT's tenant_id and never sees another tenant's rows.
        var stored = await db.CustomerSettings
            .ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, ct);

        // Driven by the declared catalogue, not by the table: a key the seeder has not written
        // yet still shows up with its factory value (IsDefault = true), and a stale row for a
        // key that no longer exists in the catalogue is simply not exposed.
        // Listed while the module is in force or deactivated (read-only); hidden if never subscribed.
        var banking = await modules.IsReadableAsync(TenantModules.BankingMicrofinance, ct);

        var items = CustomerSettingKeys.Defaults
            .Where(declared => banking || !CustomerSettingKeys.BankingMicrofinanceKeys.Contains(declared.Key))
            .Select(declared => stored.TryGetValue(declared.Key, out var row)
                ? CustomerSettingDto.FromStored(row, declared)
                : CustomerSettingDto.FromDefault(declared))
            .ToList();

        return Result.Ok<IReadOnlyList<CustomerSettingDto>>(items);
    }
}
