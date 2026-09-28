using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.ContactPoints.AddContactPoint;
using Sankore.Modules.Customers.Features.ContactPoints.CloseContactPoint;
using Sankore.Modules.Customers.Features.ContactPoints.ListContactPoints;
using Sankore.Modules.Customers.Features.ContactPoints.PromoteContactPointToPrimary;

namespace Sankore.Modules.Customers.Features.ContactPoints;

/// <summary>
/// Area aggregator for the contact-point slices (US-M01-BE-09). Mounted under
/// <c>api/v1/clients/{clientId}/contact-points</c> by
/// <c>CustomersModule.MapCustomersModuleEndpoints()</c>.
/// </summary>
public static class ContactPointsEndpoints
{
    public static IEndpointRouteBuilder MapContactPointsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("clients/{clientId:guid}/contact-points")
            .WithTags("Client Contact Points");

        return group
            .MapAddContactPoint()
            .MapListContactPoints()
            .MapPromoteContactPointToPrimary()
            .MapCloseContactPoint();
    }
}
