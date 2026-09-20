namespace Sankore.Modules.Leads.Features.GetPipeline;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class GetPipelineEndpoint
{
    public static IEndpointRouteBuilder MapGetPipeline(this IEndpointRouteBuilder app)
    {
        app.MapGet("pipeline", Handle)
            .WithName("GetPipeline")
            .WithTags("Pipeline")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<PipelineView>()
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender, CancellationToken ct,
        Guid? agencyId = null, Guid? ownerId = null, int maxPerStage = 50)
    {
        var result = await sender.Send(
            new GetPipelineQuery(agencyId, ownerId, maxPerStage), ct);

        return Results.Ok(result.Value);
    }
}
