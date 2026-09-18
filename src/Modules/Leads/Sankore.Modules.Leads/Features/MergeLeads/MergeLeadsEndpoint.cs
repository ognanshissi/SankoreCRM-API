namespace Sankore.Modules.Leads.Features.MergeLeads;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class MergeLeadsEndpoint
{
    public static IEndpointRouteBuilder MapMergeLeads(this IEndpointRouteBuilder app)
    {
        app.MapPost("{targetLeadId:guid}/merge", Handle)
            .WithName("MergeLeads")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanMergeLeads.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid targetLeadId,
        MergeLeadsRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var mergedBy = http.User.GetUserId();

        var result = await sender.Send(
            new MergeLeadsCommand(targetLeadId, req.SourceLeadId, mergedBy), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.Problem(title: "Lead merge failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record MergeLeadsRequest(Guid SourceLeadId);
