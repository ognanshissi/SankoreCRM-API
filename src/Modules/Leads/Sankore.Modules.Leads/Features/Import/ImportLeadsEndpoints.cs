namespace Sankore.Modules.Leads.Features.Import;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Import.ImportFromFile;
using Sankore.Modules.Leads.Features.Import.ImportFromGoogleContacts;
using Sankore.Modules.Leads.Features.Import.ImportFromGoogleSheet;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Multi-source lead import: uploaded file (CSV/Excel), Google Sheet, Google Contacts.
/// Mirrors the Administration module's user import.
/// </summary>
public static class ImportLeadsEndpoints
{
    private static readonly string[] AllowedExtensions = [".csv", ".xlsx"];
    private const long MaxFileSize = 5 * 1024 * 1024; // 5 MB

    public static IEndpointRouteBuilder MapImportLeadsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("import").WithTags("Lead Import");

        group.MapPost("file", ImportFromFileEndpoint)
            .WithName("ImportLeadsFromFile")
            .RequireAuthorization(Permissions.CanImportLeads.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ImportLeadsAccepted>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .DisableAntiforgery();

        group.MapPost("google-sheet", ImportFromGoogleSheetEndpoint)
            .WithName("ImportLeadsFromGoogleSheet")
            .RequireAuthorization(Permissions.CanImportLeads.Code)
            .Produces<ImportLeadsAccepted>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        group.MapPost("google-contacts", ImportFromGoogleContactsEndpoint)
            .WithName("ImportLeadsFromGoogleContacts")
            .RequireAuthorization(Permissions.CanImportLeads.Code)
            .Produces<ImportLeadsAccepted>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    internal static async Task<IResult> ImportFromFileEndpoint(
        IFormFile file,
        IFileStore fileStore,
        ISender sender,
        HttpContext http,
        string? interestedProduct,
        string? preferredLanguage,
        LeadSource? source,
        CancellationToken ct)
    {
        var validationProblem = ValidateFile(file);
        if (validationProblem is not null) return validationProblem;

        await using var stream = file.OpenReadStream();
        var fileRef = await fileStore.StoreAsync(stream, file.FileName, ct);

        var result = await sender.Send(new ImportFromFileCommand(
            http.User.GetTenantId(),
            http.User.GetUserId(),
            fileRef,
            file.FileName,
            new ImportDefaults(interestedProduct, preferredLanguage, source)), ct);

        return Accepted(result);
    }

    private static async Task<IResult> ImportFromGoogleSheetEndpoint(
        GoogleSheetImportRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new ImportFromGoogleSheetCommand(
            http.User.GetTenantId(),
            http.User.GetUserId(),
            req.SpreadsheetUrl,
            new ImportDefaults(req.InterestedProduct, req.PreferredLanguage, req.Source)), ct);

        return Accepted(result);
    }

    private static async Task<IResult> ImportFromGoogleContactsEndpoint(
        GoogleContactsImportRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new ImportFromGoogleContactsCommand(
            http.User.GetTenantId(),
            http.User.GetUserId(),
            new ImportDefaults(req.InterestedProduct, req.PreferredLanguage, req.Source)), ct);

        return Accepted(result);
    }

    internal static IResult? ValidateFile(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return Results.Problem(
                title: "Invalid file type",
                detail: $"Only CSV and Excel (.xlsx) files are accepted. Received: {ext}",
                statusCode: 400);

        if (file.Length == 0)
            return Results.Problem(
                title: "Empty file",
                detail: "The uploaded file is empty.",
                statusCode: 400);

        if (file.Length > MaxFileSize)
            return Results.Problem(
                title: "File too large",
                detail: $"Maximum file size is {MaxFileSize / (1024 * 1024)} MB.",
                statusCode: 400);

        return null;
    }

    private static IResult Accepted(Result<ImportLeadsAccepted> result)
        => result.IsSuccess
            ? Results.Accepted($"/api/v1/leads/import/{result.Value.ImportJobId}", result.Value)
            : Results.Problem(
                title: "Import scheduling failed",
                detail: result.Error,
                statusCode: 422);
}

/// <param name="SpreadsheetUrl">Full spreadsheet URL or bare spreadsheet id.</param>
public sealed record GoogleSheetImportRequest(
    string SpreadsheetUrl,
    string? InterestedProduct = null,
    string? PreferredLanguage = null,
    LeadSource? Source = null);

/// <param name="InterestedProduct">Required: contacts carry no product of their own.</param>
public sealed record GoogleContactsImportRequest(
    string InterestedProduct,
    string? PreferredLanguage = null,
    LeadSource? Source = null);
