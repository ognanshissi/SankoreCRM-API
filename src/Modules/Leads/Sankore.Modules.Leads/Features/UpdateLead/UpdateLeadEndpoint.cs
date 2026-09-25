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
            ExpectedUpdatedAt: req.ExpectedUpdatedAt), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                "LEAD_NOT_FOUND" => Results.NotFound(),
                "CONFLICT"       => Results.Conflict(new { error = result.Error }),
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
    DateTimeOffset? ExpectedUpdatedAt = null);
