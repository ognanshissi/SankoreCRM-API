namespace Sankore.Modules.Leads.Features.Settings;

using Sankore.Modules.Leads.Domain;

/// <summary>
/// One tenant M13 parameter as an administration screen needs it: the value in force, what it is
/// for, and whether it is still the factory one. Same shape as M01's <c>customer-settings</c> and
/// M02's <c>kyc-settings</c>, because these are the same kind of object.
/// </summary>
/// <param name="ValueType">
/// <c>int</c>, <c>bool</c>, <c>decimal</c>, <c>string</c> or <c>json</c> — it drives the admin
/// widget, so a screen does not keep its own table of which key is a number.
/// </param>
/// <param name="DefaultValue">
/// The compiled-in default, exposed so a screen can offer "revenir à la valeur d'origine" without
/// hard-coding 24 or 90.
/// </param>
/// <param name="IsDefault">
/// True when the value in force equals the factory one, whether or not a row exists.
/// </param>
public sealed record LeadSettingDto(
    string Key,
    string Value,
    string ValueType,
    string Description,
    string DefaultValue,
    bool IsDefault,
    DateTimeOffset? UpdatedAt,
    Guid? UpdatedBy)
{
    /// <summary>
    /// A declared key the tenant has no row for. It answers with the factory value because that
    /// is what every reader resolves to — <c>LeadSettingsService</c> falls back to it — so showing
    /// nothing would make the screen disagree with the module.
    /// </summary>
    public static LeadSettingDto FromDefault(LeadSettingDefault declared) => new(
        Key: declared.Key,
        Value: declared.Value,
        ValueType: declared.ValueType,
        Description: declared.Description,
        DefaultValue: declared.Value,
        IsDefault: true,
        UpdatedAt: null,
        UpdatedBy: null);

    /// <summary>
    /// The stored row, with the type and the description taken from the CATALOGUE rather than
    /// from the row: a row written before a description was reworded would otherwise keep showing
    /// the old wording for ever.
    /// </summary>
    public static LeadSettingDto FromStored(LeadSetting stored, LeadSettingDefault declared) => new(
        Key: declared.Key,
        Value: stored.Value,
        ValueType: declared.ValueType,
        Description: declared.Description,
        DefaultValue: declared.Value,
        IsDefault: string.Equals(stored.Value, declared.Value, StringComparison.Ordinal),
        UpdatedAt: stored.UpdatedAt,
        UpdatedBy: stored.UpdatedBy);
}
