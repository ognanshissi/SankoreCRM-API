namespace Sankore.Modules.Integration.Features.Insurance.CreateInsuranceProduct;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class CreateInsuranceProductEndpoint
{
    internal static IEndpointRouteBuilder MapCreateInsuranceProduct(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("CreateInsuranceProduct")
            .WithSummary("Add an insurance product to the tenant's catalogue")
            .WithDescription(
                "Created INACTIVE, like a connection: a product is configured in several steps "
                + "and must not be offerable between the first save and the last — activate it "
                + "with POST {productId}/activate. The insurer (`connectionId`) and its product "
                + "code are the product's identity and cannot be changed afterwards; re-pointing "
                + "is a new product. A `linkedCreditProductCode` must name a LOAN product of "
                + "M12's catalogue (ASS-03, borrower's insurance). "
                + "404 INTEGRATION_CONNECTION_NOT_FOUND for an unknown connection — never 403; "
                + "422 INSURANCE_CONNECTION_WRONG_FAMILY when it is a core-banking one; "
                + "409 INSURANCE_PRODUCT_ALREADY_EXISTS when that insurer already has the code. "
                + "Requires permission: Ins.Product.Manage.")
            .RequireAuthorization(Permissions.CanManageInsuranceProduct.Code)
            .Produces<InsuranceProductDto>(StatusCodes.Status201Created)
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
        CreateInsuranceProductRequest request, ISender sender, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(
            new CreateInsuranceProductCommand(
                request.ConnectionId, request.InsurerProductCode, request.Product),
            ct);

        if (result.IsSuccess)
            // The `api/v1` prefix is part of the location, as CreateConnectionEndpoint writes it:
            // the module's groups are mounted under it, so a location without it points nowhere.
            return Results.Created(
                $"api/v1/integration/insurance/products/{result.Value!.Id}", result.Value);

        return InsuranceProductResults.Problem(result.Error);
    }
}

/// <summary>
/// Body of the create. The identity fields sit beside the shared body rather than inside it, so
/// the update can reuse the body without exposing them.
/// </summary>
public sealed record CreateInsuranceProductRequest(
    Guid ConnectionId,
    string InsurerProductCode,
    InsuranceProductWriteRequest Product);
