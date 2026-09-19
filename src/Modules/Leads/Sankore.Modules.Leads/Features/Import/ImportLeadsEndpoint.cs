namespace Sankore.Modules.Leads.Features.Import;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ImportLeadsEndpoint
{
    private static readonly string[] AllowedExtensions = [".csv"];
    private const long MaxFileSize = 5 * 1024 * 1024; // 5 MB

    public static IEndpointRouteBuilder MapImportLeads(this IEndpointRouteBuilder app)
    {
        app.MapPost("import", Handle)
            .WithName("ImportLeads")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanImportLeads.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ImportLeadsAccepted>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> Handle(
        IFormFile file,
        IImportFileStore fileStore,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        // ── Validate the uploaded file ───────────────────────────────
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return Results.Problem(
                title: "Invalid file type",
                detail: $"Only CSV files are accepted. Received: {ext}",
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

        // ── Store the file (opaque reference) ────────────────────────
        var tenantId = http.User.GetTenantId();
        var userId = http.User.GetUserId();

        await using var stream = file.OpenReadStream();
        var fileRef = await fileStore.StoreAsync(stream, file.FileName, ct);

        // ── Schedule the background import ───────────────────────────
        var result = await sender.Send(
            new ImportLeadsCommand(tenantId, userId, fileRef, file.FileName), ct);

        return result.IsSuccess
            ? Results.Accepted(
                $"/api/v1/leads/import/{result.Value.ImportJobId}",
                result.Value)
            : Results.Problem(
                title: "Import scheduling failed",
                detail: result.Error,
                statusCode: 422);
    }
}
