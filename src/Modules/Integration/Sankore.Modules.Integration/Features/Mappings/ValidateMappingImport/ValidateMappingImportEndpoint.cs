namespace Sankore.Modules.Integration.Features.Mappings.ValidateMappingImport;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Mappings.Csv;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ValidateMappingImportEndpoint
{
    public static IEndpointRouteBuilder MapValidateMappingImport(this IEndpointRouteBuilder app)
    {
        app.MapPost("{domain}/import/validate", Handle)
            .WithName("ValidateIntegrationMappingImport")
            .WithSummary("Dry-run a mapping import file")
            .WithDescription(
                "Reads the file and returns exactly the report the import would return, writing "
                + $"nothing. `domain` is one of: {MappingDomainRoute.Values}. Columns, header "
                + $"required: {MappingCsvColumns.Header}. .csv only. "
                + "Run this first: finding out about a column of swapped codes after they reached "
                + "the core banking system is the expensive way. "
                + "The uploaded file is deleted either way. "
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
            new ValidateMappingImportCommand(
                connectionId, parsed, stored.Reference!, DeleteAfterwards: true), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest)
        };
    }
}
