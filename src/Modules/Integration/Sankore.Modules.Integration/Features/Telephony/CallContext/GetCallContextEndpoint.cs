namespace Sankore.Modules.Integration.Features.Telephony.CallContext;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetCallContextEndpoint
{
    internal static IEndpointRouteBuilder MapGetCallContext(this IEndpointRouteBuilder app)
    {
        app.MapGet("calls/{callId:guid}/context", Handle)
            .WithName("GetTelephonyCallContext")
            .WithSummary("Who is on this call, for the screen that opens while it rings")
            .WithDescription(
                "The other half of the screen pop: the realtime push carries a call id and "
                + "nothing else, and this turns it into a name — under your permission and inside "
                + "your agency perimeter, neither of which a websocket frame can apply. "
                + "matchStatus says what to render: Matched is one candidate, Ambiguous is "
                + "several (a family handset, a shop line — the agent chooses, with POST "
                + "calls/{callId}/link), Unknown is nobody on file. A candidate with "
                + "outOfPerimeter=true carries NO displayName: the caller is known to the "
                + "institution elsewhere, which stops you searching for somebody you cannot see, "
                + "and the record stays hidden. NO TELEPHONE NUMBER is ever returned. "
                + "Requires permission: Telephony.Call.View.")
            .RequireAuthorization(Permissions.CanViewTelephonyCall.Code)
            .Produces<TelephonyCallContext>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid callId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetCallContextQuery(callId), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        return result.Error switch
        {
            TelephonyErrors.CallNotFound => Results.NotFound(new { error = result.Error }),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}
