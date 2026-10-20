namespace Sankore.Modules.Customers.Features.Import;

/// <summary>
/// Normalized row from any import source. Carries both the individual and the legal-entity
/// columns; which one is used depends on whether <see cref="LegalName"/> is filled, the same
/// rule the lead conversion applies.
///
/// Every field is a string: a spreadsheet cell has no type, and parsing belongs in one place
/// (the validator) rather than being spread across three readers.
/// </summary>
public sealed record ImportClientRow
{
    // ── Individual ──────────────────────────────────────────────────────────
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Gender { get; init; }
    public string? DateOfBirth { get; init; }
    public string? Nationality { get; init; }
    public string? IdentityDocumentType { get; init; }
    public string? IdentityDocumentNumber { get; init; }
    public string? Profession { get; init; }

    // ── Legal entity ────────────────────────────────────────────────────────
    public string? LegalName { get; init; }
    public string? LegalFormCode { get; init; }
    public string? RegistrationNumber { get; init; }
    public string? TaxIdNumber { get; init; }
    public string? IncorporationDate { get; init; }

    // ── Common ──────────────────────────────────────────────────────────────
    public string? PhoneNumber { get; init; }
    public string? Email { get; init; }
    public string? AgencyCode { get; init; }
    public string? PreferredLanguage { get; init; }
    public string? AddressStreet { get; init; }
    public string? AddressCity { get; init; }
    public string? AddressCountry { get; init; }

    /// <summary>
    /// The row's <c>custom.&lt;key&gt;</c> columns, by full header name (dynamic forms, lot 6), checked
    /// against the tenant's client form at creation — a value it refuses fails the row.
    /// </summary>
    [CsvHelper.Configuration.Attributes.Ignore]
    public IReadOnlyDictionary<string, string?> CustomFields { get; init; } = new Dictionary<string, string?>();

    /// <summary>True when the row describes a company rather than a person.</summary>
    public bool IsLegalEntity => !string.IsNullOrWhiteSpace(LegalName);
}
