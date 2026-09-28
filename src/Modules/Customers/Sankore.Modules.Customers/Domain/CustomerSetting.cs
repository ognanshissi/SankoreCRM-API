namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One tenant-level setting of module M01. Values are stored as strings and parsed by
/// <c>ICustomerSettings</c> according to <see cref="ValueType"/>; keys always come from
/// <see cref="CustomerSettingKeys"/>.
/// </summary>
public sealed class CustomerSetting
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

    private CustomerSetting() { } // EF Core

    public static CustomerSetting Create(Guid tenantId, string key, string value, string valueType, string? description)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new DomainException("Setting key is required.", "CustomerSetting.Key.Required");
        if (string.IsNullOrWhiteSpace(valueType))
            throw new DomainException("Setting value type is required.", "CustomerSetting.ValueType.Required");

        return new CustomerSetting
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Key = key.Trim(),
            Value = value ?? string.Empty,
            ValueType = valueType.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void SetValue(string value, Guid? actor)
    {
        Value = value ?? string.Empty;
        UpdatedBy = actor;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
