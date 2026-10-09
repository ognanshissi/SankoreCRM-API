namespace Sankore.Modules.Integration.Features.Insurance.GetInsuranceProduct;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetInsuranceProductEndpoint
{
    internal static IEndpointRouteBuilder MapGetInsuranceProduct(this IEndpointRouteBuilder app)
    {
        app.MapGet("{productId:guid}", Handle)
            .WithName("GetInsuranceProduct")
            .WithSummary("Read one insurance product")
            .WithDescription(
                "The full catalogue entry: guarantees, pricing, eligibility rules, the linked "
                + "credit product, the commission rate and the premium retry policy. An unknown "
                + "product, or one belonging to another tenant, answers 404 "
                + "INSURANCE_PRODUCT_NOT_FOUND — never 403. "
                + "Requires permission: Ins.Product.Manage.")
            // Ins.Product.View and not Ins.Product.Manage: reading one product is a distributor's need. ASS-11
            // separates distribution from management, and an agent who may subscribe a
            // policy but may not see which products exist has a screen that cannot be
            // drawn. The four write routes of this area keep Manage.
            .RequireAuthorization(Permissions.CanViewInsuranceProduct.Code)
            .Produces<InsuranceProductDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid productId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetInsuranceProductQuery(productId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound);
    }
}
