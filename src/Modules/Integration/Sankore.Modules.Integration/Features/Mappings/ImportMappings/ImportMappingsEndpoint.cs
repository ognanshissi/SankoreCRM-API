namespace Sankore.Modules.Integration.Features.Mappings.ImportMappings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ImportMappingsEndpoint
{
    public static IEndpointRouteBuilder MapImportMappings(this IEndpointRouteBuilder app)
    {
        app.MapPost("{domain}/import", Handle)
            .WithName("ImportIntegrationMappings")
            .WithSummary("Import a domain's code mappings from a CSV file")
            .WithDescription(
                $"`domain` is one of: {MappingDomainRoute.Values}. Columns, header required: "
                + $"{MappingCsvColumns.Header} (`label` is optional). .csv only. "
                + "Returns a per-line report. A line that fails creates NOTHING; the valid lines "
                + "around it are still imported, so one bad line does not send an operator back to "
                + "fix four hundred. A line whose CRM code already has a mapping is an update. Two "
                + "lines carrying the SAME crm_code are both refused — the file does not say which "
                + "external code was meant. "
                + "Run POST {domain}/import/validate first to see the report without writing. "
                + "An unknown connection, or one belonging to another tenant, answers 404 — never 403. "
                + "Requires permission: Integration.Mapping.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationMapping.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<MappingImportReport>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader()
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid connectionId,
        string domain,
        IFormFile file,
        IFileStore fileStore,
        ISender sender,
        CancellationToken ct)
    {
        if (!MappingDomainRoute.TryParse(domain, out var parsed))
            return Results.Problem(
                $"Unknown mapping domain '{domain}'. Expected one of: {MappingDomainRoute.Values}.",
                statusCode: StatusCodes.Status400BadRequest);

        var stored = await MappingUploads.StoreAsync(file, fileStore, ct);
        if (stored.Error is { } uploadError)
            return Results.Problem(uploadError, statusCode: StatusCodes.Status400BadRequest);

        var result = await sender.Send(
            new ImportMappingsCommand(connectionId, parsed, stored.Reference!, DeleteAfterwards: true), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
            // Anything else is a header problem, and the failure carries the sentence that says
            // which column is missing — the frozen PublicApi error list has no file-level code,
            // and an operator needs the column name, not INTEGRATION_PAYLOAD_INVALID.
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest)
        };
    }
}
