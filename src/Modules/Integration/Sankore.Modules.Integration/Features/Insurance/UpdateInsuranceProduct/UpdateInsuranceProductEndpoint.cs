namespace Sankore.Modules.Integration.Features.Insurance.UpdateInsuranceProduct;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class UpdateInsuranceProductEndpoint
{
    internal static IEndpointRouteBuilder MapUpdateInsuranceProduct(this IEndpointRouteBuilder app)
    {
        app.MapPut("{productId:guid}", Handle)
            .WithName("UpdateInsuranceProduct")
            .WithSummary("Edit one insurance product")
            .WithDescription(
                "Everything an administrator may change: label, guarantees, pricing, eligibility "
                + "rules, linked credit product, commission rate, premium retry policy and "
                + "validity window. The insurer and its product code are the product's identity "
                + "and are NOT editable — re-pointing is a new product. Activation is its own "
                + "route. 404 INSURANCE_PRODUCT_NOT_FOUND for an unknown or foreign product — "
                + "never 403; 409 INTEGRATION_CONCURRENCY_CONFLICT when somebody else saved "
                + "first. Requires permission: Ins.Product.Manage.")
            .RequireAuthorization(Permissions.CanManageInsuranceProduct.Code)
            .Produces<InsuranceProductDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid productId,
        InsuranceProductWriteRequest request,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateInsuranceProductCommand(productId, request), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : InsuranceProductResults.Problem(result.Error);
    }
}
