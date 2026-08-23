using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Agencies;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Agencies.GetAgency;

public static class GetAgencyEndpoint
{
    public static IEndpointRouteBuilder MapGetAgency(this IEndpointRouteBuilder app)
    {
        app.MapGet("{id:guid}", Handle)
            .WithName("GetAgency")
            .WithSummary("Get an agency by ID")
            .RequireAuthorization(Permissions.CanReadAgency.Code)
            .Produces<AgencyDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetAgencyQuery(id), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
