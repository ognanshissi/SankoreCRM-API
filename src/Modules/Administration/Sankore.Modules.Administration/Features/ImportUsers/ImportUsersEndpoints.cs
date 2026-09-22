namespace Sankore.Modules.Administration.Features.ImportUsers;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.ImportUsers.GetImportStatus;
using Sankore.Modules.Administration.Features.ImportUsers.ImportFromFile;
using Sankore.Modules.Administration.Features.ImportUsers.ImportFromGoogleContacts;
using Sankore.Modules.Administration.Features.ImportUsers.ImportFromGoogleSheet;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.FileStore;
using Sankore.Shared.Kernel;

public static class ImportUsersEndpoints
{
    public static IEndpointRouteBuilder MapImportUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("users/import").WithTags("User Import");

        group.MapPost("file", ImportFromFile)
            .WithName("ImportUsersFromFile")
            .RequireAuthorization(Permissions.CanCreateUser.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ImportJobCreatedResult>(StatusCodes.Status202Accepted)
            .WithOpenApi()
            .WithTenantHeader()
            .DisableAntiforgery();

        group.MapPost("google-sheet", ImportFromGoogleSheetEndpoint)
            .WithName("ImportUsersFromGoogleSheet")
            .RequireAuthorization(Permissions.CanCreateUser.Code)
            .Produces<ImportJobCreatedResult>(StatusCodes.Status202Accepted)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("google-contacts", ImportFromGoogleContactsEndpoint)
            .WithName("ImportUsersFromGoogleContacts")
            .RequireAuthorization(Permissions.CanCreateUser.Code)
            .Produces<ImportJobCreatedResult>(StatusCodes.Status202Accepted)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{id:guid}/status", GetImportStatusEndpoint)
            .WithName("GetUserImportStatus")
            .RequireAuthorization(Permissions.CanCreateUser.Code)
            .Produces<UserImportStatusDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> ImportFromFile(
        IFormFile file,
        IFileStore fileStore,
        ICurrentUser currentUser,
        ITenantContext tenant,
        ISender sender,
        CancellationToken ct)
    {
        if (file.Length == 0)
            return Results.Problem("File is empty.", statusCode: 400);

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".csv" or ".xlsx"))
            return Results.Problem("Only .csv and .xlsx files are supported.", statusCode: 400);

        await using var stream = file.OpenReadStream();
        var fileRef = await fileStore.StoreAsync(stream, file.FileName, ct);

        var result = await sender.Send(new ImportFromFileCommand(
            tenant.CurrentTenantId, currentUser.Id, fileRef, file.FileName), ct);

        return result.IsSuccess
            ? Results.Accepted($"users/import/{result.Value}/status", new ImportJobCreatedResult(result.Value))
            : Results.Problem(result.Error, statusCode: 422);
    }

    private static async Task<IResult> ImportFromGoogleSheetEndpoint(
        GoogleSheetImportRequest req,
        ICurrentUser currentUser,
        ITenantContext tenant,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ImportFromGoogleSheetCommand(
            tenant.CurrentTenantId, currentUser.Id, req.SpreadsheetUrl), ct);

        return result.IsSuccess
            ? Results.Accepted($"users/import/{result.Value}/status", new ImportJobCreatedResult(result.Value))
            : Results.Problem(result.Error, statusCode: 422);
    }

    private static async Task<IResult> ImportFromGoogleContactsEndpoint(
        ICurrentUser currentUser,
        ITenantContext tenant,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ImportFromGoogleContactsCommand(
            tenant.CurrentTenantId, currentUser.Id), ct);

        return result.IsSuccess
            ? Results.Accepted($"users/import/{result.Value}/status", new ImportJobCreatedResult(result.Value))
            : Results.Problem(result.Error, statusCode: 422);
    }

    private static async Task<IResult> GetImportStatusEndpoint(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetImportStatusQuery(id), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound();
    }
}

public sealed record ImportJobCreatedResult(Guid ImportJobId);
public sealed record GoogleSheetImportRequest(string SpreadsheetUrl);
