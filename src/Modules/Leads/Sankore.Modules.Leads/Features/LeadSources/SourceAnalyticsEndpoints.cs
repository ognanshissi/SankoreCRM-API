namespace Sankore.Modules.Leads.Features.LeadSources;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.LeadSources.ExportDuplicates;
using Sankore.Modules.Leads.Features.LeadSources.ListRuns;
using Sankore.Modules.Leads.Features.LeadSources.SourceQuality;
using Sankore.Shared.Kernel;

public static class SourceAnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapSourceAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /lead-sources/{id}/runs
        app.MapGet("lead-sources/{id:guid}/runs", ListRuns)
            .WithName("ListLeadSourceRuns")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanReadLeadSources.Code)
            .Produces<PagedResult<RunDto>>()
            .WithOpenApi();

        // GET /lead-sources/quality
        app.MapGet("lead-sources/quality", GetQuality)
            .WithName("GetSourceQuality")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanViewLeadAnalytics.Code)
            .Produces<IReadOnlyList<SourceQualityDto>>()
            .WithOpenApi();

        // GET /lead-sources/{id}/duplicates.csv
        app.MapGet("lead-sources/{id:guid}/duplicates.csv", ExportDuplicates)
            .WithName("ExportSourceDuplicates")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanReadLeadSources.Code)
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListRuns(
        Guid id, ISender sender, CancellationToken ct,
        int page = 1, int pageSize = 20)
    {
        var result = await sender.Send(new ListRunsQuery(id, page, pageSize), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetQuality(
        ISender sender, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var result = await sender.Send(new SourceQualityQuery(from, to), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> ExportDuplicates(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ExportDuplicatesQuery(id), ct);

        if (!result.IsSuccess)
            return Results.NotFound();

        return Results.File(
            result.Value.CsvBytes,
            contentType: "text/csv",
            fileDownloadName: result.Value.FileName);
    }
}
