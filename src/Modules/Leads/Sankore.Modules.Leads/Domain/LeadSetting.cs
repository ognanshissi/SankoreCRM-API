namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One tenant-level setting of module M13. Values are stored as strings and parsed by
/// <c>ILeadSettings</c> according to <see cref="ValueType"/>; keys always come from
/// <see cref="LeadSettingKeys"/>.
///
/// <para>
/// <b>Why a store at all, when <c>Leads:*</c> configuration already exists.</b>
/// <c>LeadModuleSettings</c> binds the <c>Leads</c> section, which is per DEPLOYMENT: changing
/// <c>QualificationThreshold</c> changes it for every tenant of the host at once. The parameters
/// arriving with the household/compliance work are per INSTITUTION — how long a health
/// questionnaire is kept after transmission is a retention policy an institution answers for — so
/// they cannot live in the same place. Same shape as M01's <c>customer_settings</c> and M02's
/// <c>kyc_settings</c>, on purpose: three modules that each own their parameters because M12
/// exposes no generic settings store.
/// </para>
/// </summary>
public sealed class LeadSetting : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Key { get; private set; } = default!;
    public string Value { get; private set; } = default!;

    /// <summary>One of "string", "int", "bool", "decimal", "json".</summary>
    public string ValueType { get; private set; } = default!;

    public string? Description { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    private LeadSetting() { } // EF Core

    public static LeadSetting Create(
        Guid tenantId, string key, string value, string valueType, string? description)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new DomainException("Setting key is required.", "LeadSetting.Key.Required");
        if (string.IsNullOrWhiteSpace(valueType))
            throw new DomainException("Setting value type is required.", "LeadSetting.ValueType.Required");

        return new LeadSetting
        {
            Id        = Guid.NewGuid(),
            TenantId  = tenantId,
            Key       = key.Trim(),
            Value     = value ?? string.Empty,
            ValueType = valueType.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void SetValue(string value, Guid? actor)
    {
        Value     = value ?? string.Empty;
        UpdatedBy = actor;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
