namespace Sankore.Modules.Integration.Features.Insurance.QuoteInsuranceProduct;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class QuoteInsuranceProductEndpoint
{
    internal static IEndpointRouteBuilder MapQuoteInsuranceProduct(this IEndpointRouteBuilder app)
    {
        app.MapPost("{productId:guid}/quote", Handle)
            .WithName("QuoteInsuranceProduct")
            .WithSummary("Price one insurance product for one customer")
            .WithDescription(
                "ASS-03's two branches behind one question: a catalogue-priced product answers "
                + "from the catalogue with no insurer call, an insurer-priced one is quoted "
                + "through IInsuranceProductPort when the connection's adapter declares the "
                + "PriceProduct capability. `source` says which. A product that is not offerable "
                + "is refused BEFORE any insurer call with 422 "
                + "INSURANCE_PRODUCT_NOT_OFFERABLE and its reasons; a missing capability answers "
                + "422 INTEGRATION_CAPABILITY_NOT_SUPPORTED; an insurer outage answers the "
                + "adapter's own code so a transient failure stays distinguishable from a "
                + "refusal. 404 INSURANCE_PRODUCT_NOT_FOUND for an unknown or foreign product — "
                + "never 403. Requires permission: Ins.Product.Manage.")
            // Ins.Product.View and not Ins.Product.Manage: asking what a product costs is part of selling it, not of administering the catalogue. ASS-11
            // separates distribution from management, and an agent who may subscribe a
            // policy but may not see which products exist has a screen that cannot be
            // drawn. The four write routes of this area keep Manage.
            .RequireAuthorization(Permissions.CanViewInsuranceProduct.Code)
            .Produces<InsuranceQuoteDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid productId,
        QuoteInsuranceProductRequest request,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(
            new QuoteInsuranceProductQuery(productId, request.CrmCustomerId, request.InsuredAmount),
            ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : InsuranceProductResults.Problem(result.Error);
    }
}

/// <summary>
/// Body of the quote. The customer is required even for a catalogue-priced product: the port
/// takes it, and an insurer tariff is a function of the person.
/// </summary>
public sealed record QuoteInsuranceProductRequest(Guid CrmCustomerId, decimal? InsuredAmount = null);
