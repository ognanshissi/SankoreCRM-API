namespace Sankore.Modules.Leads.Features.CaptureLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;

public static class CaptureLeadEndpoint
{
    public static IEndpointRouteBuilder MapCaptureLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/leads", Handle)
            .WithName("CaptureLead")
            .WithTags("Leads")
            .RequireAuthorization("Leads.Capture")
            .Produces<CaptureLeadResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi();

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
            TenantId: tenantId,
            FullName: req.FullName,
            PhoneNumber: req.PhoneNumber,
            Source: req.Source,
            InterestedProduct: req.InterestedProduct,
            PreferredLanguage: req.PreferredLanguage,
            Latitude: req.Latitude,
            Longitude: req.Longitude,
            PreferredAgencyId: req.PreferredAgencyId), ct);

        return result.IsSuccess
            ? Results.Created($"/api/leads/{result.Value.LeadId}", result.Value)
            : Results.Problem(title: "Lead capture failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record CaptureLeadRequest(
    string FullName,
    string PhoneNumber,
    Sankore.Modules.Leads.Domain.LeadSource Source,
    string InterestedProduct,
    string PreferredLanguage,
    double Latitude,
    double Longitude,
    Guid? PreferredAgencyId);
