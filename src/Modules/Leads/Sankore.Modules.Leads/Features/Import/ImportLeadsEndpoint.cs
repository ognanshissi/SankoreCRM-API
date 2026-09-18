namespace Sankore.Modules.Leads.Features.Import;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ImportLeadsEndpoint
{
    public static IEndpointRouteBuilder MapImportLeads(this IEndpointRouteBuilder app)
    {
        app.MapPost("import", Handle)
            .WithName("ImportLeads")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanImportLeads.Code)
            .Produces<LeadImportResult>()
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ImportLeadsRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var tenantId = http.User.GetTenantId();

        var result = await sender.Send(new ImportLeadsCommand(tenantId, req.Rows), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(title: "Lead import failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record ImportLeadsRequest(IReadOnlyList<ImportLeadRow> Rows);
