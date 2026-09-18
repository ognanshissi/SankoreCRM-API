namespace Sankore.Modules.Leads.Features.RecordFirstContact;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class RecordFirstContactEndpoint
{
    public static IEndpointRouteBuilder MapRecordFirstContact(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/first-contact", Handle)
            .WithName("RecordFirstContact")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanAssignLead.Code)
            .Produces(StatusCodes.Status204NoContent)
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

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(
                    title: "Record first contact failed",
                    detail: result.Error,
                    statusCode: 422);
    }
}

/// <param name="ContactedAt">Exact time of contact. Defaults to server time if omitted.</param>
public sealed record RecordFirstContactRequest(DateTimeOffset? ContactedAt = null);
