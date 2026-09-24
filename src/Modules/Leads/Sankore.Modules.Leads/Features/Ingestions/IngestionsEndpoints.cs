namespace Sankore.Modules.Leads.Features.Ingestions;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Ingestions.GetIngestionPayload;
using Sankore.Modules.Leads.Features.Ingestions.ListIngestions;
using Sankore.Modules.Leads.Features.Ingestions.ReplayIngestion;
using Sankore.Shared.Kernel;

public static class IngestionsEndpoints
{
    public static IEndpointRouteBuilder MapIngestionsEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /lead-sources/{sourceId}/ingestions
        app.MapGet("lead-sources/{sourceId:guid}/ingestions", ListIngestions)
            .WithName("ListIngestions")
            .WithTags("Ingestions")
            .RequireAuthorization(Permissions.CanReadLeadSources.Code)
            .Produces<PagedResult<IngestionDto>>()
            .WithOpenApi();

        // GET /ingestions/{id}/payload (audited)
        app.MapGet("ingestions/{id:guid}/payload", GetPayload)
            .WithName("GetIngestionPayload")
            .WithTags("Ingestions")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces<IngestionPayloadDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST /ingestions/{id}/replay
        app.MapPost("ingestions/{id:guid}/replay", Replay)
            .WithName("ReplayIngestion")
            .WithTags("Ingestions")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListIngestions(
        Guid sourceId, ISender sender, CancellationToken ct,
        LeadIngestionStatus? status = null, int page = 1, int pageSize = 20)
    {
        var result = await sender.Send(
            new ListIngestionsQuery(sourceId, status, page, pageSize), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetPayload(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetIngestionPayloadCommand(id), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> Replay(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ReplayIngestionCommand(id), ct);

        return result.IsSuccess
            ? Results.Accepted()
            : result.Error == "INGESTION_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(detail: result.Error, statusCode: 422);
    }
}
