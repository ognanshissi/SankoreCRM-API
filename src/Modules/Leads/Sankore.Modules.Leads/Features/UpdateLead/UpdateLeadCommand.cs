namespace Sankore.Modules.Leads.Features.UpdateLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed record UpdateLeadCommand(
    Guid LeadId,
    string? FullName,
    string? FirstName,
    string? LastName,
    string? Email,
    LeadGender? Gender,
    DateOnly? DateOfBirth,
    string? InterestedProduct,
    decimal? DesiredAmount,
    string? DesiredCurrency,
    string? PreferredLanguage,
    string? Campaign,
    string? Comment,
    double? Latitude,
    double? Longitude,
    Guid? PreferredAgencyId,
    /// <summary>UpdatedAt the caller last read; null skips the staleness check.</summary>
    DateTimeOffset? ExpectedUpdatedAt = null,
    /// <summary>Custom-field changes, merged — see <c>UpdateLeadRequest.CustomFields</c>.</summary>
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? CustomFields = null,
    string? CompanyName = null,
    string? CompanyEmail = null,
    string? CompanyPhone = null,
    string? Website = null,
    LeadType? ProspectType = null,
    string? ActivitySector = null,
    string? CompanySize = null,
    decimal? EstimatedAnnualPurchases = null,
    bool ClearEstimatedAnnualPurchases = false,
    string? ContactJobTitle = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
