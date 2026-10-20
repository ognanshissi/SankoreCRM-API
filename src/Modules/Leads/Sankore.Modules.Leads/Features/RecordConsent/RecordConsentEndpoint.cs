namespace Sankore.Modules.Leads.Features.RecordConsent;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class RecordConsentEndpoint
{
    public static IEndpointRouteBuilder MapRecordConsent(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/consents", Handle)
            .WithName("RecordConsent")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanRecordConsent.Code)
            .Produces<RecordConsentResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        RecordConsentRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var tenantId   = http.User.GetTenantId();
        var recordedBy = http.User.GetUserId();

        var result = await sender.Send(new RecordConsentCommand(
            TenantId:       tenantId,
            LeadId:         leadId,
            Type:           req.Type,
            Channel:        req.Channel,
            RecordedBy:     recordedBy,
            ProofReference: req.ProofReference,
            Granted:        req.Granted,
            ProofActivityId: req.ProofActivityId), ct);

        if (!result.IsSuccess)
        {
            return result.Error is "LEAD_NOT_FOUND" or RecordConsentHandler.ActivityNotFound
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Record consent failed", detail: result.Error, statusCode: 422);
        }

        return Results.Created(
            $"/api/v1/leads/{leadId}/consents/{result.Value!.ConsentId}",
            result.Value);
    }
}

/// <param name="Type">Purpose for which consent is collected.</param>
/// <param name="Channel">Medium through which consent was obtained.</param>
/// <param name="ProofReference">Optional auditable reference: URL to recorded call, form ID, IP address, etc.</param>
/// <param name="Granted">False records a REFUSAL — the prospect was asked and said no.</param>
/// <param name="ProofActivityId">The logged call or visit during which the prospect answered.</param>
public sealed record RecordConsentRequest(
    ConsentType Type,
    ConsentChannel Channel,
    string? ProofReference = null,
    bool Granted = true,
    Guid? ProofActivityId = null);
