namespace Sankore.Modules.Leads.Features.ExportLeads;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

public static class ExportLeadsEndpoint
{
    public static IEndpointRouteBuilder MapExportLeads(this IEndpointRouteBuilder app)
    {
        app.MapGet("export", Handle)
            .WithName("ExportLeads")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanExportLeads.Code)
            .Produces<byte[]>(contentType: "text/csv")
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        LeadStatus? status = null,
        PipelineStage? pipelineStage = null,
        LeadSource? source = null,
        Guid? ownerId = null,
        Guid? agencyId = null,
        string? search = null,
        string? tag = null)
    {
        var result = await sender.Send(
            new ExportLeadsQuery(status, pipelineStage, source, ownerId, agencyId, search, tag), ct);

        return result.IsSuccess
            ? Results.File(result.Value, "text/csv; charset=utf-8", "leads.csv")
            : Results.Problem(title: "Export failed", detail: result.Error, statusCode: 500);
    }
}
