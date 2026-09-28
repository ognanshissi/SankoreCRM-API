namespace Sankore.Modules.Customers.Features.Compliance.Shared;

/// <summary>
/// Error codes owned by the Compliance zone only.
/// <para>
/// Everything in the module-wide contract lives in
/// <see cref="Sankore.Modules.Customers.Domain.CustomerErrors"/> and must be referenced from
/// there. The two codes below are additions this zone needs and that the shared contract does
/// not define: the settings contract only names <c>SETTING_UNKNOWN</c> (unknown key) and says
/// nothing about a value that does not parse as its declared type, nor about a value that
/// breaks a per-key floor (the 10-year regulatory minimum on <c>retention-years</c>).
/// </para>
/// <para>
/// They are a defensive second line only: the validator rejects both cases first, so a caller
/// normally sees a 400 with the field-level message rather than one of these codes.
/// </para>
/// </summary>
internal static class ComplianceErrors
{
    /// <summary>The value does not parse as the key's declared <c>ValueType</c>.</summary>
    public const string SettingValueInvalid = "SETTING_VALUE_INVALID";

    /// <summary>The value parses but breaks a hard floor/ceiling attached to the key.</summary>
    public const string SettingValueOutOfRange = "SETTING_VALUE_OUT_OF_RANGE";
}
