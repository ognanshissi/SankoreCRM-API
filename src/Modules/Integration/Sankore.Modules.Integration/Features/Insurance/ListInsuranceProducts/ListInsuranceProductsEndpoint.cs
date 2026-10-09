namespace Sankore.Modules.Integration.Features.Insurance.ListInsuranceProducts;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListInsuranceProductsEndpoint
{
    internal static IEndpointRouteBuilder MapListInsuranceProducts(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListInsuranceProducts")
            .WithSummary("List the insurance products this tenant distributes")
            .WithDescription(
                "The tenant's catalogue, grouped by insurer. Every row carries `isOfferable` and, "
                + "when false, `notOfferableReasons` — one of PRODUCT_INACTIVE, "
                + "CONNECTION_INACTIVE, CONNECTION_WRONG_FAMILY, NOT_YET_EFFECTIVE, "
                + "NO_LONGER_EFFECTIVE, INSURER_PRICING_UNAVAILABLE. Offerability is DERIVED, "
                + "never stored: deactivating an insurer's connection stops its products being "
                + "offerable in the same instant (ASS-03). `offerableOnly=true` pre-filters on "
                + "what the database can decide, so a returned row may still be not-offerable for "
                + "the pricing reason — branch on `isOfferable`, not on the filter. "
                + "Requires permission: Ins.Product.Manage.")
            // Ins.Product.View and not Ins.Product.Manage: the catalogue an agent chooses from. ASS-11
            // separates distribution from management, and an agent who may subscribe a
            // policy but may not see which products exist has a screen that cannot be
            // drawn. The four write routes of this area keep Manage.
            .RequireAuthorization(Permissions.CanViewInsuranceProduct.Code)
            .Produces<PagedResult<InsuranceProductDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        CancellationToken ct,
        Guid? connectionId = null,
        bool? isActive = null,
        bool offerableOnly = false,
        int page = 1,
        int pageSize = 50)
        => Results.Ok((await sender.Send(
            new ListInsuranceProductsQuery(connectionId, isActive, offerableOnly, page, pageSize),
            ct)).Value);
}
