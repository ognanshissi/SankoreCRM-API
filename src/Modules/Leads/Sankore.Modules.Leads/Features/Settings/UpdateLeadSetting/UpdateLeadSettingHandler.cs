namespace Sankore.Modules.Leads.Features.Settings.UpdateLeadSetting;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Persistence goes through <see cref="ILeadSettings"/> rather than straight to the table: that
/// service is what every reader resolves through, it owns the closed list and the type check, and
/// it evicts its own process-local cache entry on write. Writing around it would leave readers on
/// a stale value for up to the cache TTL — a minute in which the old retention period still
/// governs an irreversible job.
/// </summary>
internal sealed class UpdateLeadSettingHandler(
    LeadsDbContext db,
    ILeadSettings settings,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<UpdateLeadSettingCommand, Result<LeadSettingDto>>
{
    public async Task<Result<LeadSettingDto>> Handle(
        UpdateLeadSettingCommand command, CancellationToken ct)
    {
        var declared = LeadSettingKeys.Find(command.Key);
        if (declared is null)
            return Result.Fail<LeadSettingDto>(LeadSettingErrors.Unknown);

        var value = command.Value.Trim();

        var write = await settings.SetAsync(
            currentUser.TenantId, declared.Key, value, currentUser.Id, ct);

        if (write.IsFailure)
            return Result.Fail<LeadSettingDto>(write.Error!);

        var stored = await db.LeadSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == declared.Key, ct);

        // A null row means the settings service inserted through a second DbContext that has not
        // surfaced in this one yet. The value written is still the authoritative one, so the
        // response is built from it rather than answering "unknown" for something just saved.
        return Result.Ok(stored is not null
            ? LeadSettingDto.FromStored(stored, declared)
            : new LeadSettingDto(
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
