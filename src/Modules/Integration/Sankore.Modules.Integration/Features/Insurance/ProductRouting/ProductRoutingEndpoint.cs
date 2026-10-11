namespace Sankore.Modules.Integration.Features.Insurance.ProductRouting;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ProductRoutingEndpoint
{
    internal static IEndpointRouteBuilder MapProductRouting(this IEndpointRouteBuilder app)
    {
        app.MapGet("product-routing", Handle)
            .WithName("GetInsuranceProductRouting")
            .WithSummary("Which insurer each insurance product line would be subscribed with")
            .WithDescription(
                "The preflight of BNF-B-16. For every ACTIVE insurance line of the tenant's "
                + "catalogue: the insurer that would receive its subscription, and how that was "
                + "decided — Mapped, SingleConnectionFallback, Unroutable or "
                + "NoInsuranceConnection. `unroutableCount` above zero means those lines' "
                + "subscriptions are refused with MappingMissing today, and the holding stays in "
                + "PendingSync with only a log line to say so. `crmCode` is the value a mapping "
                + "row must carry: the product's id, not its code. "
                + "Requires permission: Ins.Product.View.")
            // The read permission, not Product.Manage: this is the screen that tells somebody a
            // mapping is missing, and needing the right to CHANGE the catalogue in order to SEE
            // that would hide the fault from the people who notice it first.
            .RequireAuthorization(Permissions.CanViewInsuranceProduct.Code)
            .Produces<ProductRoutingDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
        => Results.Ok((await sender.Send(new ProductRoutingQuery(), ct)).Value);
}
