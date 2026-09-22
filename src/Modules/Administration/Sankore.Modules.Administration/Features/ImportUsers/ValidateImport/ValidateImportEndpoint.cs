namespace Sankore.Modules.Administration.Features.ImportUsers.ValidateImport;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.FileStore;
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
        IFileStore fileStore,
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

        try
        {
            var result = await sender.Send(
                new ValidateImportCommand(tenant.CurrentTenantId, fileRef), ct);

            return Results.Ok(result.Value);
        }
        finally
        {
            await fileStore.DeleteAsync(fileRef, ct);
        }
    }
}
