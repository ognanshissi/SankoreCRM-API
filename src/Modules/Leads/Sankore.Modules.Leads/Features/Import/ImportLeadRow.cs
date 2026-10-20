namespace Sankore.Modules.Leads.Features.Import;

/// <summary>
/// Raw row from any import source (CSV, Excel, Google Sheets, Google Contacts).
/// Deliberately all-string: Excel and Sheets only ever hand back text, and typed
/// binding would abort the whole file on a single malformed cell. Conversion and
/// per-row error reporting happen in <see cref="LeadRowParser"/>.
/// </summary>
public sealed record ImportLeadRow
{
    public string? FullName { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? PhoneNumber { get; init; }
    public string? Email { get; init; }

    /// <summary>
    /// National identity document number. Feeds duplicate detection and is worth 7 of the 100
    /// score points (4 profile + 3 coherence) — its absence from the import schema was pure loss.
    /// </summary>
    public string? NationalId { get; init; }
    public string? Source { get; init; }
    public string? InterestedProduct { get; init; }
    public string? PreferredLanguage { get; init; }
    public string? Latitude { get; init; }
    public string? Longitude { get; init; }
    public string? Gender { get; init; }
    public string? DateOfBirth { get; init; }
    public string? DesiredAmount { get; init; }
    public string? DesiredCurrency { get; init; }
    public string? Campaign { get; init; }
    public string? Channel { get; init; }
    public string? Comment { get; init; }
    public string? ExternalReference { get; init; }
    public string? CompanyName { get; init; }
    public string? OwnerId { get; init; }
    public string? AgencyId { get; init; }

    /// <summary>
    /// The row's <c>custom.&lt;key&gt;</c> columns, by full header name (dynamic forms, lot 6). Read as
    /// text and checked against the tenant's lead form at capture, like every other cell: a value
    /// the form refuses fails the row with the field named, as an unreadable date does.
    /// </summary>
    [CsvHelper.Configuration.Attributes.Ignore]
    public IReadOnlyDictionary<string, string?> CustomFields { get; init; } = new Dictionary<string, string?>();
}
