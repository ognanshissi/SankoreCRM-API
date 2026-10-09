namespace Sankore.Modules.Integration.Features.Insurance;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Insurance.ActivateInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.CreateInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.DeactivateInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.GetInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.ListInsuranceProducts;
using Sankore.Modules.Integration.Features.Insurance.QuoteInsuranceProduct;
using Sankore.Modules.Integration.Features.Insurance.UpdateInsuranceProduct;

/// <summary>
/// Area aggregator of the insurance family (ASS-03 for now; ASS-04 → ASS-10 add their slices and
/// one line each here).
///
/// <para>
/// The group is <c>integration/insurance/products</c> and the connection is NOT in the route,
/// unlike the mappings area. A product belongs to its insurer and carries its
/// <c>connectionId</c>, but the question an administrator asks is "what does my institution
/// distribute" — across insurers — and a per-connection route would make that question N requests
/// and the screen's own job to merge them. Filtering by connection is a query parameter, which is
/// what it is.
/// </para>
///
/// <para>
/// Tagged <c>Integration</c>, like every other area of the module: the front-end generator groups
/// its client by tag, and a second tag would split one module's client in two.
/// </para>
/// </summary>
internal static class InsuranceEndpoints
{
    internal static IEndpointRouteBuilder MapInsuranceEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var products = app
            .MapGroup("integration/insurance/products")
            .WithTags("Integration");

        products.MapListInsuranceProducts();
        products.MapGetInsuranceProduct();
        products.MapCreateInsuranceProduct();
        products.MapUpdateInsuranceProduct();

        // Lifecycle, as two routes rather than a boolean on the PUT: each is the switch that
        // makes a premium debitable from customers' accounts, and each earns its own audit row.
        // There is deliberately no DELETE — see DeactivateInsuranceProductCommand.
        products.MapActivateInsuranceProduct();
        products.MapDeactivateInsuranceProduct();

        // ASS-03 criterion 2, demonstrable rather than theoretical.
        products.MapQuoteInsuranceProduct();

        return app;
    }
}
