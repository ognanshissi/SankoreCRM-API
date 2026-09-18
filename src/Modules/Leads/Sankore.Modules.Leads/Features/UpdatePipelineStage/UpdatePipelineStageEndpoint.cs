namespace Sankore.Modules.Leads.Features.UpdatePipelineStage;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

public static class UpdatePipelineStageEndpoint
{
    public static IEndpointRouteBuilder MapUpdatePipelineStage(this IEndpointRouteBuilder app)
    {
        app.MapPut("{leadId:guid}/pipeline-stage", Handle)
            .WithName("UpdateLeadPipelineStage")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanMovePipelineStage.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        UpdatePipelineStageRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdatePipelineStageCommand(leadId, req.Stage), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Stage update failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record UpdatePipelineStageRequest(PipelineStage Stage);
