namespace Sankore.Modules.Kyc.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One tenant-level KYC parameter, seeded from <see cref="KycSettingKeys.Defaults"/> on tenant
/// creation and editable afterwards. The set of keys is closed: a key outside the catalogue is
/// rejected rather than stored, so a typo cannot create a parameter nothing reads.
/// </summary>
public sealed class KycSetting : AggregateRoot
{
    public Guid Id { get; private set; }
    public string Key { get; private set; } = default!;
    public string Value { get; private set; } = default!;
    public string ValueType { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    private KycSetting() { }

    public static KycSetting FromDefault(Guid tenantId, KycSettingDefault definition, TimeProvider clock)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Key = definition.Key,
            Value = definition.Value,
            ValueType = definition.ValueType,
            Description = definition.Description,
            UpdatedAt = clock.GetUtcNow(),
        };

    public void Update(string value, Guid actor, TimeProvider clock)
    {
        Value = value;
        UpdatedBy = actor;
        UpdatedAt = clock.GetUtcNow();
    }
}
