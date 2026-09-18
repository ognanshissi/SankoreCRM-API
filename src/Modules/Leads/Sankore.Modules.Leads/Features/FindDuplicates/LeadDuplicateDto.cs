namespace Sankore.Modules.Leads.Features.FindDuplicates;

using Sankore.Modules.Leads.Domain;

public sealed record LeadDuplicateDto(
    Guid LeadId,
    string FullName,
    string PhoneNumber,
    string? Email,
    LeadStatus Status,
    LeadSource Source,
    DateTimeOffset CapturedAt,
    IReadOnlyList<string> MatchedOn);
