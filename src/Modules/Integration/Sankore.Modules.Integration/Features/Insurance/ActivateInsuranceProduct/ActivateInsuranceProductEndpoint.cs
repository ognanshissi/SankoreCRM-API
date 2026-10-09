namespace Sankore.Modules.Integration.Features.Insurance.ActivateInsuranceProduct;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ActivateInsuranceProductEndpoint
{
    internal static IEndpointRouteBuilder MapActivateInsuranceProduct(this IEndpointRouteBuilder app)
    {
        app.MapPost("{productId:guid}/activate", Handle)
            .WithName("ActivateInsuranceProduct")
            .WithSummary("Put an insurance product in the sellable catalogue")
            .WithDescription(
                "Idempotent: an already-active product answers 200, not a conflict. Activating "
                + "does NOT make the product offerable on its own — the answer carries "
                + "`isOfferable` and `notOfferableReasons`, so an administrator who activates a "
                + "product whose insurer connection is deactivated is told here rather than at "
                + "the counter (ASS-03). "
                + "Requires permission: Ins.Product.Manage.")
            .RequireAuthorization(Permissions.CanManageInsuranceProduct.Code)
            .Produces<InsuranceProductDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid productId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ActivateInsuranceProductCommand(productId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : InsuranceProductResults.Problem(result.Error);
    }
}
