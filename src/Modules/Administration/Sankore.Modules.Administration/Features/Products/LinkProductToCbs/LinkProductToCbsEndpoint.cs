using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.LinkProductToCbs;

internal static class LinkProductToCbsEndpoint
{
    internal static IEndpointRouteBuilder MapLinkProductToCbs(this IEndpointRouteBuilder app)
    {
        app.MapPost("{id:guid}/link-cbs", Handle)
            .WithName("LinkProductToCbs")
            .WithSummary("Link a CRM product to a CBS product")
            .WithDescription("Associates the CRM product with an external Core Banking System product identifier. Audited separately from general product updates.")
            .RequireAuthorization(Permissions.CanUpdateProduct.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id,
        LinkProductToCbsRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new LinkProductToCbsCommand(
            id, req.BusinessProductId, req.BusinessPlatformName), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "PRODUCT_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(detail: result.Error, statusCode: 422);
    }
}

internal sealed record LinkProductToCbsRequest(
    string BusinessProductId,
    string BusinessPlatformName);
