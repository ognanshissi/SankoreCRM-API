namespace Sankore.Modules.Kyc.Features.Settings.UpdateKycSetting;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Persistence goes through <see cref="IKycSettings"/> rather than straight to the table: that
/// service is what every other slice reads through, it owns the type and range checks, and writing
/// around it would be the one place where an invalid value could land.
/// </summary>
internal sealed class UpdateKycSettingHandler(
    KycDbContext db,
    IKycSettings settings,
    IDistributedCache cache,
    ICurrentUser currentUser,
    Infrastructure.RemoteLinks.IKycOtpSender otpSender,
    TimeProvider clock)
    : IRequestHandler<UpdateKycSettingCommand, Result<KycSettingDto>>
{
    public async Task<Result<KycSettingDto>> Handle(
        UpdateKycSettingCommand command, CancellationToken ct)
    {
        var declared = KycSettingKeys.Find(command.Key);
        if (declared is null)
            return Result.Fail<KycSettingDto>(KycErrors.SettingUnknown);

        var value = command.Value.Trim();

        // KRM-B-12. The only setting in this module whose validity depends on what the HOST has
        // registered rather than on the value itself, which is why the check is here and not in
        // KycSettingRanges — a range validator cannot see the container.
        //
        // Turning the second factor on with no SMS channel would not degrade the journey, it would
        // END it: every customer of the tenant would reach a code entry screen for a message that
        // is never sent, and nothing in the logs would say why. Refused at the moment an
        // administrator tries, so they learn it instead of their customers.
        if (declared.Key.Equals(KycSettingKeys.RemoteOtp, StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(value, out var enabling) && enabling
            && !otpSender.IsAvailable)
        {
            return Result.Fail<KycSettingDto>(KycErrors.RemoteOtpUnavailable);
        }

        var write = await settings.SetAsync(currentUser.TenantId, declared.Key, value, currentUser.Id, ct);
        if (write.IsFailure)
            return Result.Fail<KycSettingDto>(write.Error!);

        // A ceiling that feeds the per-customer limits cache must drop it, for every customer of the
        // tenant and at once. Without this the new ceiling only bites as the cached entries expire —
        // up to five minutes of operations measured against the policy just revoked, which is exactly
        // the window the tier-changed consumer exists to close for a single customer.
        if (KycSettingKeys.AffectsLimits(declared.Key))
            await KycModuleFacade.InvalidateTenantLimitsAsync(cache, currentUser.TenantId, ct);

        var stored = await db.KycSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == declared.Key, ct);

        // A null row means the settings service inserted through a second DbContext that has not
        // surfaced in this one yet. The value written is still the authoritative one, so the
        // response is built from it rather than answering "unknown" for something just saved.
        return Result.Ok(stored is not null
            ? KycSettingDto.FromStored(stored, declared)
            : new KycSettingDto(
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
