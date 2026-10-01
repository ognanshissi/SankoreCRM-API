namespace Sankore.Modules.Kyc.Infrastructure.Settings;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Kernel;

internal sealed class KycSettingsService(KycDbContext db, TimeProvider clock) : IKycSettings
{
    public async Task<string> GetStringAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var stored = await db.KycSettings
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        // The compiled-in default is the floor, not a convenience: a tenant created between two
        // seeder runs would otherwise read an empty ceiling, and an empty ceiling reads as zero.
        return stored ?? KycSettingKeys.Find(key)?.Value ?? string.Empty;
    }

    public async Task<int> GetIntAsync(Guid tenantId, string key, CancellationToken ct)
        => int.TryParse(await GetStringAsync(tenantId, key, ct),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    public async Task<decimal> GetDecimalAsync(Guid tenantId, string key, CancellationToken ct)
        => decimal.TryParse(await GetStringAsync(tenantId, key, ct),
                            NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid tenantId, CancellationToken ct)
    {
        var stored = await db.KycSettings
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, ct);

        // Declared keys the tenant has no row for still answer with their default, so a caller
        // never has to know whether the seeder has run.
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in KycSettingKeys.Defaults)
            all[definition.Key] = stored.GetValueOrDefault(definition.Key, definition.Value);

        return all;
    }

    public async Task<Result> SetAsync(
        Guid tenantId, string key, string value, Guid actor, CancellationToken ct)
    {
        var definition = KycSettingKeys.Find(key);
        if (definition is null)
            return Result.Fail(KycErrors.SettingUnknown);

        if (!IsWellTyped(value, definition.ValueType))
            return Result.Fail(KycErrors.SettingInvalidValue);

        // AsTracking is not optional: the context is NoTracking by default, so without it the
        // update mutates a detached entity and SaveChangesAsync writes nothing — the screen
        // reports success and the ceiling never moves.
        var existing = await db.KycSettings
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Key == key, ct);

        if (existing is null)
            db.KycSettings.Add(KycSetting.FromDefault(tenantId, definition with { Value = value }, clock));
        else
            existing.Update(value, actor, clock);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    private static bool IsWellTyped(string value, string valueType) => valueType switch
    {
        KycSettingKeys.TypeInt =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),

        KycSettingKeys.TypeDecimal =>
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),

        KycSettingKeys.TypeBool => bool.TryParse(value, out _),

        _ => !string.IsNullOrWhiteSpace(value),
    };
}
