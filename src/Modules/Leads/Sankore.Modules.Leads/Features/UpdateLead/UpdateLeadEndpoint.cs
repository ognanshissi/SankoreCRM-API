namespace Sankore.Modules.Leads.Features.UpdateLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

public static class UpdateLeadEndpoint
{
    public static IEndpointRouteBuilder MapUpdateLead(this IEndpointRouteBuilder app)
    {
        app.MapPut("{leadId:guid}", Handle)
            .WithName("UpdateLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanUpdateLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        UpdateLeadRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateLeadCommand(
            LeadId:           leadId,
            FullName:         req.FullName,
            FirstName:        req.FirstName,
            LastName:         req.LastName,
            Email:            req.Email,
            Gender:           req.Gender,
            DateOfBirth:      req.DateOfBirth,
            InterestedProduct: req.InterestedProduct,
            DesiredAmount:    req.DesiredAmount,
            DesiredCurrency:  req.DesiredCurrency,
            PreferredLanguage: req.PreferredLanguage,
            Campaign:         req.Campaign,
            Comment:          req.Comment,
            Latitude:         req.Latitude,
            Longitude:        req.Longitude,
            PreferredAgencyId: req.PreferredAgencyId,
            ExpectedUpdatedAt: req.ExpectedUpdatedAt,
            CustomFields:     req.CustomFields,
            CompanyName:      req.CompanyName,
            CompanyEmail:     req.CompanyEmail,
            CompanyPhone:     req.CompanyPhone,
            Website:          req.Website,
            ProspectType:     req.ProspectType,
            ActivitySector:   req.ActivitySector,
            CompanySize:      req.CompanySize,
            EstimatedAnnualPurchases: req.EstimatedAnnualPurchases,
            ClearEstimatedAnnualPurchases: req.ClearEstimatedAnnualPurchases,
            ContactJobTitle:  req.ContactJobTitle), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                "LEAD_NOT_FOUND" => Results.NotFound(),
                "CONFLICT"       => Results.Conflict(new { error = result.Error }),
                "LEAD_ACTIVITY_SECTOR_INVALID" or "LEAD_COMPANY_SIZE_INVALID" or "LEAD_PROSPECT_TYPE_INVALID"
                    => Results.BadRequest(new { error = result.Error }),
                _ => Results.Problem(title: "Update failed", detail: result.Error, statusCode: 422)
            };
    }
}

public sealed record UpdateLeadRequest(
    string? FullName = null,
    string? FirstName = null,
    string? LastName = null,
    string? Email = null,
    LeadGender? Gender = null,
    DateOnly? DateOfBirth = null,
    string? InterestedProduct = null,
    decimal? DesiredAmount = null,
    string? DesiredCurrency = null,
    string? PreferredLanguage = null,
    string? Campaign = null,
    string? Comment = null,
    double? Latitude = null,
    double? Longitude = null,
    Guid? PreferredAgencyId = null,
    /// <summary>UpdatedAt read with the lead; echo it back to detect concurrent edits.</summary>
    DateTimeOffset? ExpectedUpdatedAt = null,
    /// <summary>
    /// Custom-field values to change, merged into the stored ones: a key with a value overwrites,
    /// a key with null clears, an absent key is left alone.
    /// </summary>
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? CustomFields = null,
    /// <summary>The prospect's company. Null leaves a field alone, an empty string clears it.</summary>
    string? CompanyName = null,
    string? CompanyEmail = null,
    string? CompanyPhone = null,
    string? Website = null,
    /// <summary>Person or company; null leaves it alone.</summary>
    LeadType? ProspectType = null,
    /// <summary>A sector code (options of the Lead form's `activitySector` field); null leaves it, "" clears it.</summary>
    string? ActivitySector = null,
    /// <summary>"Micro" | "Small" | "Medium" | "Large"; null leaves it, "" clears it.</summary>
    string? CompanySize = null,
    /// <summary>Expected yearly purchases, in the institution's currency; null leaves them.</summary>
    decimal? EstimatedAnnualPurchases = null,
    /// <summary>True clears the expected yearly purchases.</summary>
    bool ClearEstimatedAnnualPurchases = false,
    /// <summary>The contact's position; null leaves it, "" clears it.</summary>
    string? ContactJobTitle = null);
