namespace Sankore.Modules.Leads.Features.ConvertLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class ConvertLeadEndpoint
{
    public static IEndpointRouteBuilder MapConvertLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/convert", Handle)
            .WithName("ConvertLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanConvertLead.Code)
            .Produces<ConvertLeadResult>(StatusCodes.Status200OK)
            .Produces<ConvertLeadResult>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        ConvertLeadRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new ConvertLeadCommand(
                LeadId:                 leadId,
                CustomerId:             req.CustomerId,
                Force:                  req.Force,
                MinConfidenceThreshold: req.MinConfidenceThreshold ?? 70.0), ct);

        if (!result.IsSuccess)
        {
            return result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Conversion failed", detail: result.Error, statusCode: 422);
        }

        // Duplicate gate blocked the conversion — return 409 with the list.
        if (result.Value.DuplicateDetected)
            return Results.Conflict(result.Value);

        return Results.Ok(result.Value);
    }
}

public sealed record ConvertLeadRequest(
    /// <summary>
    /// Optional. Supply a pre-existing customer id to link (e.g. when the
    /// Customers module created the record first). Leave null for auto-generation.
    /// </summary>
    Guid? CustomerId = null,
    /// <summary>
    /// Set to true to bypass the duplicate gate and force conversion even when
    /// high-confidence duplicate leads are detected.
    /// </summary>
    bool Force = false,
    /// <summary>
    /// Minimum confidence score (0-100) for a candidate to trigger the gate.
    /// Defaults to 70 (Probable). Lower this to catch more candidates.
    /// </summary>
    double? MinConfidenceThreshold = null);
