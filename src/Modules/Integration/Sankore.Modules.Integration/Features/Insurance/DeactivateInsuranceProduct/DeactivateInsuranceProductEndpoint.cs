namespace Sankore.Modules.Integration.Features.Insurance.DeactivateInsuranceProduct;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class DeactivateInsuranceProductEndpoint
{
    internal static IEndpointRouteBuilder MapDeactivateInsuranceProduct(this IEndpointRouteBuilder app)
    {
        app.MapPost("{productId:guid}/deactivate", Handle)
            .WithName("DeactivateInsuranceProduct")
            .WithSummary("Withdraw an insurance product from the catalogue")
            .WithDescription(
                "There is no DELETE: subscriptions, policies, premium instalments and statement "
                + "lines name a product, so withdrawal is a status. Existing policies keep "
                + "running — this stops NEW subscriptions; cancelling a contract is a different "
                + "act. Idempotent. "
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
        var result = await sender.Send(new DeactivateInsuranceProductCommand(productId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : InsuranceProductResults.Problem(result.Error);
    }
}
