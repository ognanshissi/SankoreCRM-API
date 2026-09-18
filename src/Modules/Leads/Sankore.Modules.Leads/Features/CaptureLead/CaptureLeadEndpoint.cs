namespace Sankore.Modules.Leads.Features.CaptureLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class CaptureLeadEndpoint
{
    public static IEndpointRouteBuilder MapCaptureLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("", Handle)
            .WithName("CaptureLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanCaptureLead.Code)
            .Produces<CaptureLeadResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CaptureLeadRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var tenantId = http.User.GetTenantId();

        var result = await sender.Send(new CaptureLeadCommand(
            TenantId:             tenantId,
            FullName:             req.FullName,
            PhoneNumber:          req.PhoneNumber,
            Source:               req.Source,
            InterestedProduct:    req.InterestedProduct,
            PreferredLanguage:    req.PreferredLanguage,
            Latitude:             req.Latitude,
            Longitude:            req.Longitude,
            PreferredAgencyId:    req.PreferredAgencyId,
            FirstName:            req.FirstName,
            LastName:             req.LastName,
            Email:                req.Email,
            Gender:               req.Gender ?? LeadGender.Unknown,
            DateOfBirth:          req.DateOfBirth,
            DesiredAmount:        req.DesiredAmount,
            DesiredCurrency:      req.DesiredCurrency,
            Campaign:             req.Campaign,
            Channel:              req.Channel,
            Comment:              req.Comment,
            ExternalReference:    req.ExternalReference,
            OwnerId:              req.OwnerId,
            AgencyId:             req.AgencyId,
            AgentCollectedLeadId: req.AgentCollectedLeadId,
            CompanyName:          req.CompanyName,
            CompanyEmail:         req.CompanyEmail,
            CompanyPhone:         req.CompanyPhone,
            Website:              req.Website), ct);

        return result.IsSuccess
            ? Results.Created($"/api/leads/{result.Value.LeadId}", result.Value)
            : Results.Problem(title: "Lead capture failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record CaptureLeadRequest(
    string FullName,
    string PhoneNumber,
    LeadSource Source,
    string InterestedProduct,
    string PreferredLanguage,
    double Latitude,
    double Longitude,
    Guid? PreferredAgencyId,
    string? FirstName = null,
    string? LastName = null,
    string? Email = null,
    LeadGender? Gender = null,
    DateOnly? DateOfBirth = null,
    decimal? DesiredAmount = null,
    string? DesiredCurrency = null,
    string? Campaign = null,
    LeadChannel? Channel = null,
    string? Comment = null,
    string? ExternalReference = null,
    Guid? OwnerId = null,
    Guid? AgencyId = null,
    Guid? AgentCollectedLeadId = null,
    string? CompanyName = null,
    string? CompanyEmail = null,
    string? CompanyPhone = null,
    string? Website = null);
