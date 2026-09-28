using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Relationships.AddRelationship;
using Sankore.Modules.Customers.Features.Relationships.CloseRelationship;
using Sankore.Modules.Customers.Features.Relationships.ListRelationships;

namespace Sankore.Modules.Customers.Features.Relationships;

/// <summary>
/// Area aggregator for the relationship slices (US-M01-BE-22). Mounted under
/// <c>api/v1/clients/{clientId}/relationships</c> by
/// <c>CustomersModule.MapCustomersModuleEndpoints()</c>.
/// </summary>
public static class RelationshipsEndpoints
{
    public static IEndpointRouteBuilder MapRelationshipsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("clients/{clientId:guid}/relationships")
            .WithTags("Client Relationships");

        return group
            .MapAddRelationship()
            .MapListRelationships()
            .MapCloseRelationship();
    }
}
