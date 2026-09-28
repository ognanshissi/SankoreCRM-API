namespace Sankore.Modules.Customers.Features.Clients;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;
using Sankore.Modules.Customers.Features.Clients.GetClient;
using Sankore.Modules.Customers.Features.Clients.RevealSensitiveField;
using Sankore.Modules.Customers.Features.Clients.SearchClients;
using Sankore.Modules.Customers.Features.Clients.UpdateClient;
using Sankore.Modules.Customers.Features.Clients.UpdateClientSensitive;

/// <summary>
/// Aggregator of the <c>clients</c> route group. Adding a slice to this zone touches its
/// own folder plus exactly one line here.
/// </summary>
public static class ClientsEndpoints
{
    public static IEndpointRouteBuilder MapClientsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("clients").WithTags("Clients");

        // Order matters for the literal-vs-parameter routes below: "clients/legal" (the
        // LegalEntities zone) must not be shadowed by "clients/{clientId:guid}" — the
        // :guid constraint is what keeps them apart, so keep it on every parameter route.
        return group
            .MapCreateIndividualClient()
            .MapSearchClients()
            .MapGetClient()
            .MapUpdateClient()
            .MapUpdateClientSensitive()
            .MapRevealSensitiveField();
    }
}
