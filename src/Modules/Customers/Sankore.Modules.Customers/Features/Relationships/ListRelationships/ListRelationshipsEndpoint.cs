using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Relationships.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.Relationships.ListRelationships;

public static class ListRelationshipsEndpoint
{
    public static IEndpointRouteBuilder MapListRelationships(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListClientRelationships")
            .WithSummary("List a client's relationships")
            .WithDescription(
                "Returns the client's links to other clients and to external persons. " +
                "An external person's phone, date of birth and document number are masked. " +
                "Pass includeClosed=true to also return closed relationships (those with a " +
                "validTo date). Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<IReadOnlyList<RelationshipDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid clientId,
        bool includeClosed,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListRelationshipsQuery(clientId, includeClosed), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : RelationshipHttp.ToProblem(result.Error);
    }
}
