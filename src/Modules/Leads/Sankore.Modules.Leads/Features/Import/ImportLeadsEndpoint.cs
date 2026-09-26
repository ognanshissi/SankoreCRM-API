namespace Sankore.Modules.Leads.Features.Import;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

/// <summary>
/// Kept as an alias of <c>POST leads/import/file</c> so existing clients keep working.
/// New integrations should call the source-specific routes in
/// <see cref="ImportLeadsEndpoints"/>.
/// </summary>
public static class ImportLeadsEndpoint
{
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

    private static Task<IResult> Handle(
        IFormFile file,
        IFileStore fileStore,
        ISender sender,
        HttpContext http,
        string? interestedProduct,
        string? preferredLanguage,
        CancellationToken ct)
        => ImportLeadsEndpoints.ImportFromFileEndpoint(
            file, fileStore, sender, http, interestedProduct, preferredLanguage, source: null, ct);
}
