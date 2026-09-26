namespace Sankore.Modules.Leads.Features.Import;

using Sankore.Modules.Leads.Domain;

/// <summary>
/// Values applied to rows that leave a field blank. Supplied when the import is
/// started and persisted on the job: Google Contacts carries no product, language
/// or source, so without these every contact row would fail validation.
/// </summary>
public sealed record ImportDefaults(
    string? InterestedProduct = null,
    string? PreferredLanguage = null,
    LeadSource? Source = null);
