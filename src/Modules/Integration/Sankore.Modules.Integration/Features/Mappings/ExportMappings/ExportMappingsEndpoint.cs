namespace Sankore.Modules.Integration.Features.Mappings.ExportMappings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ExportMappingsEndpoint
{
    public static IEndpointRouteBuilder MapExportMappings(this IEndpointRouteBuilder app)
    {
        app.MapGet("{domain}/export.csv", Handle)
            .WithName("ExportIntegrationMappings")
            .WithSummary("Export a domain's code mappings as CSV")
            .WithDescription(
                $"`domain` is one of: {MappingDomainRoute.Values}. Columns: "
                + $"{MappingCsvColumns.Header} — the same three the import reads, so the file can "
                + "be corrected in a spreadsheet and uploaded back. UTF-8 with a BOM, because "
                + "Excel otherwise mangles accented labels. "
                + "A domain with no mapping yet exports the header alone: that is the template. "
                + "An unknown connection, or one belonging to another tenant, answers 404 — never 403. "
                + "Requires permission: Integration.Mapping.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationMapping.Code)
            .Produces<byte[]>(StatusCodes.Status200OK, MappingExportCsv.ContentType)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid connectionId,
        string domain,
        ISender sender,
        CancellationToken ct)
    {
        if (!MappingDomainRoute.TryParse(domain, out var parsed))
            return Results.Problem(
                $"Unknown mapping domain '{domain}'. Expected one of: {MappingDomainRoute.Values}.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await sender.Send(new ExportMappingsQuery(connectionId, parsed), ct);

        if (result.IsSuccess)
            return Results.File(result.Value.Content, result.Value.ContentType, result.Value.FileName);

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest)
        };
    }
}
