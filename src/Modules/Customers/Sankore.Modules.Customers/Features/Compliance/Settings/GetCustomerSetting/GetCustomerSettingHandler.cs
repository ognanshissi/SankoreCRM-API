namespace Sankore.Modules.Customers.Features.Compliance.Settings.GetCustomerSetting;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetCustomerSettingHandler(CustomersDbContext db)
    : IRequestHandler<GetCustomerSettingQuery, Result<CustomerSettingDto>>
{
    public async Task<Result<CustomerSettingDto>> Handle(
        GetCustomerSettingQuery query, CancellationToken ct)
    {
        var declared = CustomerSettingValueValidation.FindDefault(query.Key);
        if (declared is null)
            return Result.Fail<CustomerSettingDto>(CustomerErrors.SettingUnknown);

        var stored = await db.CustomerSettings
            .FirstOrDefaultAsync(s => s.Key == declared.Key, ct);

        return Result.Ok(stored is null
            ? CustomerSettingDto.FromDefault(declared)
            : CustomerSettingDto.FromStored(stored, declared));
    }
}
