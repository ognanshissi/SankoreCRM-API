namespace Sankore.Modules.Customers.Features.Compliance.Shared;

using Sankore.Modules.Customers.Domain;

/// <summary>
/// One tenant setting as returned by the read endpoints.
/// <para>
/// <see cref="ValueType"/> drives the widget the admin screen renders and is authoritative:
/// it always comes from <see cref="CustomerSettingKeys.Defaults"/>, never from the stored row,
/// so a legacy row written with the wrong type cannot mislead the UI.
/// </para>
/// <para>
/// <see cref="IsDefault"/> is <c>true</c> when the tenant has never moved the key away from its
/// factory value — including when no row exists yet, which is what an un-seeded tenant looks
/// like. The screen uses it to show "inherited" instead of "customized".
/// </para>
/// </summary>
public sealed record CustomerSettingDto(
    string Key,
    string Value,
    string ValueType,
    string? Description,
    string DefaultValue,
    bool IsDefault,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy)
{
    /// <summary>Projects a stored row, falling back to the declared default for metadata.</summary>
    internal static CustomerSettingDto FromStored(CustomerSetting stored, CustomerSettingDefault declared) =>
        new(
            Key: declared.Key,
            Value: stored.Value,
            ValueType: declared.ValueType,
            Description: declared.Description,
            DefaultValue: declared.Value,
            IsDefault: string.Equals(stored.Value, declared.Value, StringComparison.Ordinal),
            UpdatedAt: stored.UpdatedAt,
            UpdatedBy: stored.UpdatedBy);

    /// <summary>Projects a key the tenant has no row for yet (not seeded, or seeded later).</summary>
    internal static CustomerSettingDto FromDefault(CustomerSettingDefault declared) =>
        new(
            Key: declared.Key,
            Value: declared.Value,
            ValueType: declared.ValueType,
            Description: declared.Description,
            DefaultValue: declared.Value,
            IsDefault: true,
            UpdatedAt: null,
            UpdatedBy: null);
}
