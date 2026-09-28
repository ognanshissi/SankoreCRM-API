namespace Sankore.Modules.Customers.Features.Compliance.Settings.UpdateCustomerSetting;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Persistence is delegated to <see cref="ICustomerSettings"/> rather than written straight to
/// the table: that service is what every other slice reads through (and caches behind), so
/// writing around it would leave a stale value in memory until the next restart.
/// </summary>
internal sealed class UpdateCustomerSettingHandler(
    CustomersDbContext db,
    ICustomerSettings settings,
    ICurrentUser currentUser,
    TimeProvider clock
) : IRequestHandler<UpdateCustomerSettingCommand, Result<CustomerSettingDto>>
{
    public async Task<Result<CustomerSettingDto>> Handle(
        UpdateCustomerSettingCommand command, CancellationToken ct)
    {
        var declared = CustomerSettingValueValidation.FindDefault(command.Key);
        if (declared is null)
            return Result.Fail<CustomerSettingDto>(CustomerErrors.SettingUnknown);

        // Defensive re-check: the validator already refused these, but a job or another module
        // can dispatch the command directly, outside the HTTP pipeline.
        if (CustomerSettingValueValidation.Validate(declared.Key, command.Value) is not null)
        {
            return Result.Fail<CustomerSettingDto>(
                CustomerSettingValueValidation.IsOutOfRange(declared.Key, command.Value)
                    ? ComplianceErrors.SettingValueOutOfRange
                    : ComplianceErrors.SettingValueInvalid);
        }

        var value = command.Value.Trim();

        var write = await settings.SetAsync(currentUser.TenantId, declared.Key, value, currentUser.Id, ct);
        if (write.IsFailure)
            return Result.Fail<CustomerSettingDto>(write.Error!);

        var stored = await db.CustomerSettings
            .FirstOrDefaultAsync(s => s.Key == declared.Key, ct);

        // A null row here means the settings service inserted through a second context that has
        // not flushed into this one yet; the value written is still authoritative.
        return Result.Ok(stored is not null
            ? CustomerSettingDto.FromStored(stored, declared)
            : new CustomerSettingDto(
                Key: declared.Key,
                Value: value,
                ValueType: declared.ValueType,
                Description: declared.Description,
                DefaultValue: declared.Value,
                IsDefault: string.Equals(value, declared.Value, StringComparison.Ordinal),
                UpdatedAt: clock.GetUtcNow(),
                UpdatedBy: currentUser.Id));
    }
}
