namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// A single field mapping rule: one inbound source field → one Lead target field,
/// with an optional value transformation.
/// </summary>
/// <remarks>
/// Replaces the v1 string-expression dictionary (<c>{"$.phone": "phoneNumber|e164:CI"}</c>).
/// Stored inside <see cref="SourceSettings"/> as JSONB; v1 rows are rewritten on read by
/// <see cref="SourceSettingsUpgrader.MigrateJson"/>.
/// </remarks>
public sealed record FieldMappingRule
{
    /// <summary>JSONPath (or bare property name) into the inbound payload.</summary>
    public string SourceField { get; init; } = default!;

    /// <summary>Lead target field — one of <c>FieldMappingEngine.ValidTargetFields</c>.</summary>
    public string TargetField { get; init; } = default!;

    /// <summary>Transformation applied to the extracted value.</summary>
    public FieldTransformation Transformation { get; init; } = FieldTransformation.None;

    /// <summary>Used when the source field is absent or empty.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Country for <see cref="FieldTransformation.E164"/>: ISO-3166 alpha-2 ("CI")
    /// or dial prefix ("+225" / "225").
    /// </summary>
    public string? E164Country { get; init; }

    /// <summary>Lookup table for <see cref="FieldTransformation.Map"/>. Keys matched case-insensitively.</summary>
    public IReadOnlyDictionary<string, string>? MapEntries { get; init; }

    /// <summary>
    /// Separator used when several rules feed the same target field. Default: a single space.
    /// An empty string joins without a separator.
    /// </summary>
    public string? ConcatSeparator { get; init; }
}

/// <summary>Value transformation applied by the field mapping engine.</summary>
public enum FieldTransformation
{
    /// <summary>Use the extracted value verbatim.</summary>
    None,

    /// <summary>Strip leading/trailing whitespace.</summary>
    Trim,

    /// <summary>Trim and uppercase (invariant).</summary>
    Upper,

    /// <summary>Trim and lowercase (invariant).</summary>
    Lower,

    /// <summary>Normalize a phone number to E.164 using <see cref="FieldMappingRule.E164Country"/>.</summary>
    E164,

    /// <summary>Translate the value through <see cref="FieldMappingRule.MapEntries"/>.</summary>
    Map,

    /// <summary>Join with the other rules targeting the same field, using <see cref="FieldMappingRule.ConcatSeparator"/>.</summary>
    Concat
}
