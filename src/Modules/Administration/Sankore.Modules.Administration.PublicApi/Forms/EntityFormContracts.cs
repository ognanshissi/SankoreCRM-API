namespace Sankore.Modules.Administration.PublicApi.Forms;

using Sankore.Shared.Kernel;

/// <summary>
/// The three entities whose form a tenant lays out (dynamic forms, lot 1).
///
/// <para>
/// <b>One layout per entity, not one per kind.</b> A lead is a person or a company, a client is an
/// individual or a legal entity, a product belongs to a category — and the layout is still ONE
/// document: a field that only means something for one kind says so with
/// <see cref="FormField.VisibleWhen"/> on the kind field (<c>prospectType</c>, <c>clientType</c>,
/// <c>category</c>). Two layouts would have to be kept in step by hand for every field the kinds
/// share, which is most of them.
/// </para>
/// </summary>
public enum FormEntity
{
    Lead,
    Client,
    Product,
}

public enum FormFieldType
{
    Text,
    LongText,
    Integer,
    Decimal,
    /// <summary>An amount and its ISO 4217 currency: <c>{ "amount": 1500000, "currency": "XOF" }</c>.</summary>
    Money,
    Percent,
    /// <summary><c>yyyy-MM-dd</c>.</summary>
    Date,
    /// <summary>ISO 8601 with an offset.</summary>
    DateTime,
    Boolean,
    SingleChoice,
    /// <summary>An array of option values.</summary>
    MultiChoice,
    Email,
    Phone,
    Url,
    /// <summary>
    /// A pointer to another record (an agency, a user, a product…). Native fields only in lot 1:
    /// the value is an id the owning module already validates, and a custom field pointing at a
    /// record needs a referential check this lot does not have.
    /// </summary>
    Reference,
}

public enum FormReferenceTarget
{
    Agency,
    User,
    Product,
    LeadSource,
    LegalForm,
    Country,
    Currency,
    Language,
}

public sealed record FormOption(string Value, string Label);

/// <summary>
/// Shown only when <paramref name="FieldKey"/> holds one of <paramref name="Values"/>. For a
/// boolean the values are <c>"true"</c> / <c>"false"</c>; for a multi-choice, any overlap counts.
/// </summary>
public sealed record FormCondition(string FieldKey, IReadOnlyList<string> Values);

/// <param name="QuickAdd">
/// Whether the section's TITLE appears on the creation form. It does not pull its fields in: each
/// field carries its own <see cref="FormField.QuickAdd"/>, and a quick-add field of a section that
/// is not shown appears without a heading.
/// </param>
public sealed record FormSection(
    string Key,
    string Title,
    int Order,
    bool QuickAdd = false,
    string? Description = null,
    FormCondition? VisibleWhen = null);

/// <param name="Key">
/// Stable identifier: the native property name (<c>firstName</c>) or a key chosen when the custom
/// field was created. Custom values are stored under it, so it never changes once saved.
/// </param>
/// <param name="QuickAdd">On the creation form. Always true for a required field — see <see cref="IsRequired"/>.</param>
/// <param name="IsRequired">
/// Required on every form, hence also on the creation form: a required field left off it would make
/// every creation fail on the server.
/// </param>
/// <param name="IsHidden">A native field the tenant does not use. Custom fields are archived instead.</param>
/// <param name="IsArchived">
/// A custom field no longer collected. Its values are kept and still returned; nothing can write
/// it. A custom field is never deleted once saved, because the layout service cannot see whether a
/// lead, a client or a product holds a value for it.
/// </param>
/// <param name="IsSensitive">Masked in lists and cleared on anonymisation.</param>
/// <param name="IsNative">Read only — set by the server from its catalogue.</param>
/// <param name="IsRequiredLocked">
/// Read only. Required by the entity itself (a lead's phone number): it cannot be made optional,
/// hidden or taken off the creation form.
/// </param>
public sealed record FormField(
    string Key,
    FormFieldType Type,
    string Label,
    int Order,
    string? SectionKey = null,
    bool QuickAdd = false,
    bool IsRequired = false,
    bool IsHidden = false,
    bool IsArchived = false,
    string? HelpText = null,
    string? Placeholder = null,
    IReadOnlyList<FormOption>? Options = null,
    decimal? Min = null,
    decimal? Max = null,
    int? MaxLength = null,
    string? Pattern = null,
    string? DefaultValue = null,
    bool IsSensitive = false,
    FormCondition? VisibleWhen = null,
    FormReferenceTarget? ReferenceTarget = null,
    bool IsNative = false,
    bool IsRequiredLocked = false);

/// <param name="Version">
/// 0 while the tenant has never saved a layout — the server's default one is returned. Sent back as
/// <c>expectedVersion</c> when saving, so two administrators editing at once cannot overwrite each
/// other.
/// </param>
/// <param name="Currency">
/// The institution's currency, which every Money value written through this form must be in (or
/// keep the one it already has). Null — a test double, a form read without its tenant — leaves
/// Money values as sent, a bare amount taking <see cref="InstitutionCurrencies.Default"/>.
/// </param>
public sealed record EntityFormDto(
    FormEntity Entity,
    int Version,
    IReadOnlyList<FormSection> Sections,
    IReadOnlyList<FormField> Fields,
    string? Currency = null);
