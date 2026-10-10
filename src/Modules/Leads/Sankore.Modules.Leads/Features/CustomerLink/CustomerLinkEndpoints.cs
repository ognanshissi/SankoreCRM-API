namespace Sankore.Modules.Leads.Features.CustomerLink;

using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.CustomerLink.AttachLeadToCustomer;
using Sankore.Modules.Leads.Features.CustomerLink.DetachLeadFromCustomer;
using Sankore.Modules.Leads.Features.CustomerLink.GetCustomerMatches;

internal static class CustomerLinkEndpoints
{
    public static IEndpointRouteBuilder MapCustomerLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGetCustomerMatches();
        app.MapAttachLeadToCustomer();
        app.MapDetachLeadFromCustomer();

        return app;
    }
}
