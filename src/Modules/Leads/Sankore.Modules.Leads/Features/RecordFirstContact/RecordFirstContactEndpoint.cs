namespace Sankore.Modules.Leads.Features.RecordFirstContact;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel;

public static class RecordFirstContactEndpoint
{
    public static IEndpointRouteBuilder MapRecordFirstContact(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/first-contact", Handle)
            .WithName("RecordFirstContact")
            .WithTags("Leads")
            // Was lead:assign, a supervisor's right: no agent could say « 1er contact eu » on their
            // own lead. The per-lead check is LeadAccessGuard, in the handler.
            .RequireAuthorization(Permissions.CanLogLeadActivity.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        RecordFirstContactRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new RecordFirstContactCommand(leadId, req.ContactedAt), ct);

        if (result.IsSuccess)
            return Results.NoContent();

        return result.Error switch
        {
            "LEAD_NOT_FOUND" => Results.NotFound(),
            LeadAccessGuard.Forbidden => Results.Problem(title: "Not your lead", detail: result.Error, statusCode: 403),
            _ => Results.Problem(title: "Record first contact failed", detail: result.Error, statusCode: 422),
        };
    }
}

/// <param name="ContactedAt">Exact time of contact. Defaults to server time if omitted.</param>
public sealed record RecordFirstContactRequest(DateTimeOffset? ContactedAt = null);
