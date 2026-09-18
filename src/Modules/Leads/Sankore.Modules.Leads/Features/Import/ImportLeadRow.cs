namespace Sankore.Modules.Leads.Features.Import;

using Sankore.Modules.Leads.Domain;

public sealed record ImportLeadRow(
    string FullName,
    string PhoneNumber,
    LeadSource Source,
    string InterestedProduct,
    string PreferredLanguage,
    double Latitude,
    double Longitude,
    string? FirstName = null,
    string? LastName = null,
    string? Email = null,
    LeadGender Gender = LeadGender.Unknown,
    DateOnly? DateOfBirth = null,
    decimal? DesiredAmount = null,
    string? DesiredCurrency = null,
    string? Campaign = null,
    LeadChannel? Channel = null,
    string? Comment = null,
    string? ExternalReference = null,
    Guid? OwnerId = null,
    Guid? AgencyId = null);
