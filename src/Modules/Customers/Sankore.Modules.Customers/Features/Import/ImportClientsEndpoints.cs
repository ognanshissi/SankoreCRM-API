namespace Sankore.Modules.Customers.Features.Import;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Import.GetImportStatus;
using Sankore.Modules.Customers.Features.Import.ImportFromFile;
using Sankore.Modules.Customers.Features.Import.ImportFromGoogleSheet;
using Sankore.Modules.Customers.Features.Import.ValidateImport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ImportClientsEndpoints
{
    /// <summary>
    /// Columns of the CSV/Excel template, shared by every source and by the OpenAPI description
    /// so an operator does not have to guess them.
    /// </summary>
    private const string TemplateColumns =
        "FirstName, LastName, Gender, DateOfBirth, Nationality, IdentityDocumentType, "
        + "IdentityDocumentNumber, Profession, LegalName, LegalFormCode, RegistrationNumber, "
        + "TaxIdNumber, IncorporationDate, PhoneNumber, Email, AgencyCode, PreferredLanguage, "
        + "AddressStreet, AddressCity, AddressCountry";

    public static IEndpointRouteBuilder MapImportClientsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("clients/import").WithTags("Client Import");

        group.MapPost("validate", Validate)
            .WithName("ValidateClientImport")
            .WithSummary("Dry-run a client import file")
            .WithDescription(
                "Reads the file and reports, row by row, what would happen — creating nothing. "
                + "Run this before importing: finding out about four hundred bad rows afterwards "
                + $"is the expensive way. Columns: {TemplateColumns}. "
                + "A row with a LegalName is treated as a company. Requires permission: customers:create.")
            .RequireAuthorization(Permissions.CanCreateCustomer.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ValidateClientImportResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi()
            .WithTenantHeader()
            .DisableAntiforgery();

        group.MapPost("file", ImportFromFile)
            .WithName("ImportClientsFromFile")
            .WithSummary("Import clients from a CSV or Excel file")
            .WithDescription(
                "Queues a background import. Every row goes through the same create command an "
                + "agent uses, so duplicate checks, encryption, client-number allocation and the "
                + "audit trail all apply. Poll the status endpoint for the outcome. "
                + $"Columns: {TemplateColumns}. Requires permission: customers:create.")
            .RequireAuthorization(Permissions.CanCreateCustomer.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ClientImportJobCreatedResult>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi()
            .WithTenantHeader()
            .DisableAntiforgery();

        group.MapPost("google-sheet", ImportFromGoogleSheet)
            .WithName("ImportClientsFromGoogleSheet")
            .WithSummary("Import clients from a Google Sheets spreadsheet")
            .WithDescription(
                $"Same columns as the file import: {TemplateColumns}. "
                + "Requires permission: customers:create.")
            .RequireAuthorization(Permissions.CanCreateCustomer.Code)
            .Produces<ClientImportJobCreatedResult>(StatusCodes.Status202Accepted)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{importJobId:guid}/status", GetStatus)
            .WithName("GetClientImportStatus")
            .WithSummary("Progress and per-row failures of an import")
            .RequireAuthorization(Permissions.CanCreateCustomer.Code)
            .Produces<ClientImportStatusDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Validate(
        IFormFile file, IFileStore fileStore, ITenantContext tenant, ISender sender, CancellationToken ct)
    {
        var stored = await StoreAsync(file, fileStore, ct);
        if (stored.Error is { } error)
            return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);

        var result = await sender.Send(
            new ValidateClientImportCommand(tenant.CurrentTenantId, stored.Reference!, DeleteAfterwards: true), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ImportFromFile(
        IFormFile file,
        Guid? defaultAgencyId,
        IFileStore fileStore,
        ICurrentUser currentUser,
        ITenantContext tenant,
        ISender sender,
        CancellationToken ct)
    {
        var stored = await StoreAsync(file, fileStore, ct);
        if (stored.Error is { } error)
            return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);

        var result = await sender.Send(new ImportClientsFromFileCommand(
            tenant.CurrentTenantId, currentUser.Id, stored.Reference!, file.FileName, defaultAgencyId), ct);

        return result.IsSuccess
            ? Results.Accepted(
                $"clients/import/{result.Value}/status", new ClientImportJobCreatedResult(result.Value))
            : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ImportFromGoogleSheet(
        ClientGoogleSheetImportRequest req,
        ICurrentUser currentUser,
        ITenantContext tenant,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ImportClientsFromGoogleSheetCommand(
            tenant.CurrentTenantId, currentUser.Id, req.SpreadsheetUrl, req.DefaultAgencyId), ct);

        return result.IsSuccess
            ? Results.Accepted(
                $"clients/import/{result.Value}/status", new ClientImportJobCreatedResult(result.Value))
            : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetStatus(Guid importJobId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetClientImportStatusQuery(importJobId), ct);

        return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
    }

    private static async Task<(string? Reference, string? Error)> StoreAsync(
        IFormFile file, IFileStore fileStore, CancellationToken ct)
    {
        if (file.Length == 0)
            return (null, "File is empty.");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".csv" or ".xlsx"))
            return (null, "Only .csv and .xlsx files are supported.");

        await using var stream = file.OpenReadStream();
        return (await fileStore.StoreAsync(stream, file.FileName, ct), null);
    }
}

public sealed record ClientImportJobCreatedResult(Guid ImportJobId);

/// <summary>
/// Prefixed, like the user import's own request: Swashbuckle keys OpenAPI components by simple
/// type name, so two modules cannot both expose a "GoogleSheetImportRequest".
/// </summary>
public sealed record ClientGoogleSheetImportRequest(string SpreadsheetUrl, Guid? DefaultAgencyId);
