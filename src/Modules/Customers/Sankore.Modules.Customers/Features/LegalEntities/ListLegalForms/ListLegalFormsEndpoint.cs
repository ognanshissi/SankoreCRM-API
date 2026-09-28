namespace Sankore.Modules.Customers.Features.LegalEntities.ListLegalForms;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ListLegalFormsEndpoint
{
    public static IEndpointRouteBuilder MapListLegalForms(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListLegalForms")
            .WithSummary("List the tenant's legal forms")
            .WithDescription(
                "Returns the ACTIVE legal forms by default, ordered for display; pass " +
                "includeInactive=true to also get the deactivated ones. Creating a legal " +
                "client with a code missing from the active list answers LEGAL_FORM_UNKNOWN. " +
                "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<IReadOnlyList<LegalFormDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        bool? includeInactive,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListLegalFormsQuery(includeInactive ?? false), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
