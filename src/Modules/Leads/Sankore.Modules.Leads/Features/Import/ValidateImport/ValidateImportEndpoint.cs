namespace Sankore.Modules.Leads.Features.Import.ValidateImport;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

public static class ValidateImportEndpoint
{
    public static IEndpointRouteBuilder MapValidateLeadImport(this IEndpointRouteBuilder app)
    {
        app.MapPost("import/validate", Handle)
            .WithName("ValidateLeadImport")
            .WithTags("Lead Import")
            .WithSummary("Validate an import file without capturing any lead")
            .RequireAuthorization(Permissions.CanImportLeads.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ValidateLeadImportResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi()
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> Handle(
        IFormFile file,
        IFileStore fileStore,
        ISender sender,
        string? interestedProduct,
        string? preferredLanguage,
        LeadSource? source,
        CancellationToken ct)
    {
        var problem = ImportLeadsEndpoints.ValidateFile(file);
        if (problem is not null) return problem;

        await using var stream = file.OpenReadStream();
        var fileRef = await fileStore.StoreAsync(stream, file.FileName, ct);

        try
        {
            var result = await sender.Send(new ValidateImportCommand(
                fileRef,
                new ImportDefaults(interestedProduct, preferredLanguage, source)), ct);

            return Results.Ok(result.Value);
        }
        finally
        {
            // A dry run leaves nothing behind, whatever the outcome.
            await fileStore.DeleteAsync(fileRef, ct);
        }
    }
}
