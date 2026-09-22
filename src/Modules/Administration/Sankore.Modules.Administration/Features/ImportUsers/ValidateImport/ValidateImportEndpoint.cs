namespace Sankore.Modules.Administration.Features.ImportUsers.ValidateImport;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.ImportUsers.Readers;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ValidateImportEndpoint
{
    public static IEndpointRouteBuilder MapValidateImport(this IEndpointRouteBuilder app)
    {
        app.MapPost("users/import/validate", Handle)
            .WithName("ValidateUserImport")
            .WithTags("User Import")
            .WithSummary("Validate an import file without creating users")
            .RequireAuthorization(Permissions.CanCreateUser.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ValidateImportResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi()
            .WithTenantHeader()
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> Handle(
        IFormFile file,
        AdministrationDbContext db,
        IFileStore fileStore,
        FileImportReader reader,
        ITenantContext tenant,
        CancellationToken ct)
    {
        if (file.Length == 0)
            return Results.Problem("File is empty.", statusCode: 400);

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".csv" or ".xlsx"))
            return Results.Problem("Only .csv and .xlsx files are supported.", statusCode: 400);

        // Store temporarily to reuse the same FileImportReader
        await using var stream = file.OpenReadStream();
        var fileRef = await fileStore.StoreAsync(stream, file.FileName, ct);

        try
        {
            var rows = await reader.ReadAsync(fileRef, ct);

            if (rows.Count == 0)
                return Results.Ok(new ValidateImportResponse(0, 0, 0, []));

            var validator = new ImportValidator(db);
            var result = await validator.ValidateAsync(rows, tenant.CurrentTenantId, ct);

            return Results.Ok(result);
        }
        finally
        {
            // Clean up temp file — validation only, not persisted
            await fileStore.DeleteAsync(fileRef, ct);
        }
    }
}
