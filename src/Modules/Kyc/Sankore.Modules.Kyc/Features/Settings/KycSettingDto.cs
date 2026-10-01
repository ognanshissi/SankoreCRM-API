namespace Sankore.Modules.Kyc.Features.Settings;

using Sankore.Modules.Kyc.Domain;

/// <summary>
/// One tenant KYC parameter as an administration screen needs it: the value in force, what it is
/// for, and whether it is still the factory one.
/// </summary>
/// <param name="ValueType">
/// <c>int</c>, <c>decimal</c>, <c>bool</c> or <c>string</c> — it drives the admin widget, so the
/// screen does not have to keep its own table of which key is a number.
/// </param>
/// <param name="DefaultValue">
/// The compiled-in default, exposed so a screen can offer "revenir à la valeur d'origine" without
/// hard-coding 250 000 — which is exactly what this endpoint exists to stop.
/// </param>
/// <param name="IsDefault">
/// True when the value in force equals the factory one, whether or not a row exists. A tenant that
/// never touched a parameter and a tenant that set it back to its default are the same thing here.
/// </param>
/// <param name="UpdatedAt">Null for a key no row has ever been written for.</param>
public sealed record KycSettingDto(
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
    /// A declared key the tenant has no row for. It answers with the factory value because that is
    /// what every reader resolves to — <c>KycSettingsService</c> falls back to it — so showing
    /// nothing would make the screen disagree with the module.
    /// </summary>
    public static KycSettingDto FromDefault(KycSettingDefault declared) => new(
        Key: declared.Key,
        Value: declared.Value,
        ValueType: declared.ValueType,
        Description: declared.Description,
        DefaultValue: declared.Value,
        IsDefault: true,
        UpdatedAt: null,
        UpdatedBy: null);

    /// <summary>
    /// The stored row, but with the type and the description taken from the CATALOGUE rather than
    /// from the row: a row written before a description was reworded would otherwise keep showing
    /// the old wording forever.
    /// </summary>
    public static KycSettingDto FromStored(KycSetting stored, KycSettingDefault declared) => new(
        Key: declared.Key,
        Value: stored.Value,
        ValueType: declared.ValueType,
        Description: declared.Description,
        DefaultValue: declared.Value,
        IsDefault: string.Equals(stored.Value, declared.Value, StringComparison.Ordinal),
        UpdatedAt: stored.UpdatedAt,
        UpdatedBy: stored.UpdatedBy);
}
