namespace Sankore.Modules.Leads.Features.SystemCaptureLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Lightweight capture endpoint intended for automated external integrations
/// (web forms, WhatsApp bots, SMS/USSD gateways, partner APIs, etc.).
///
/// Differences from POST leads (manual capture):
///  - InterestedProduct and PreferredLanguage are optional; defaults are applied.
///  - Duplicate gate is always bypassed (Force = true) — the calling system
///    is responsible for its own deduplication upstream.
///  - Latitude/Longitude default to 0/0 when the channel cannot provide them.
/// </summary>
public static class SystemCaptureLeadEndpoint
{
    public static IEndpointRouteBuilder MapSystemCaptureLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("system-capture", Handle)
            .WithName("SystemCaptureLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanCaptureLead.Code)
            .Produces<CaptureLeadResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        SystemCaptureLeadRequest req,
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
            InterestedProduct:    req.InterestedProduct ?? "Unknown",
            PreferredLanguage:    req.PreferredLanguage ?? "fr",
            Latitude:             req.Latitude ?? 0,
            Longitude:            req.Longitude ?? 0,
            PreferredAgencyId:    req.PreferredAgencyId,
            FirstName:            req.FirstName,
            LastName:             req.LastName,
            Email:                req.Email,
            Campaign:             req.Campaign,
            Channel:              req.Channel,
            Comment:              req.Comment,
            ExternalReference:    req.ExternalReference,
            OwnerId:              req.OwnerId,
            AgencyId:             req.AgencyId,
            ProspectType:         req.ProspectType ?? LeadType.Individual,
            NationalId:           req.NationalId,
            CustomerReference:    req.CustomerReference,
            // Always bypass duplicate confirmation — automated pipelines have no human to confirm.
            Force: true), ct);

        return result.IsSuccess
            ? Results.Created($"/api/leads/{result.Value.LeadId}", result.Value)
            : Results.Problem(title: "System lead capture failed", detail: result.Error, statusCode: 422);
    }
}

/// <summary>
/// Streamlined request contract for automated channel integrations.
/// Only FullName, PhoneNumber, Source, and Channel are required.
/// </summary>
public sealed record SystemCaptureLeadRequest(
    string FullName,
    string PhoneNumber,
    LeadSource Source,
    LeadChannel Channel,
    string? FirstName = null,
    string? LastName = null,
    string? Email = null,
    string? InterestedProduct = null,
    string? PreferredLanguage = null,
    double? Latitude = null,
    double? Longitude = null,
    Guid? PreferredAgencyId = null,
    string? Campaign = null,
    string? Comment = null,
    string? ExternalReference = null,
    Guid? OwnerId = null,
    Guid? AgencyId = null,
    LeadType? ProspectType = null,
    string? NationalId = null,
    string? CustomerReference = null);
