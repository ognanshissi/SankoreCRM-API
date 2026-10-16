namespace Sankore.Modules.Integration.Features.Telephony.LinkCall;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class LinkCallEndpoint
{
    internal static IEndpointRouteBuilder MapLinkCall(this IEndpointRouteBuilder app)
    {
        app.MapPost("calls/{callId:guid}/link", Handle)
            .WithName("LinkTelephonyCall")
            .WithSummary("Say which record a call was about, or correct it")
            .WithDescription(
                "What makes an Ambiguous call useful: the journal refuses to guess between two "
                + "people who share a number, and this is how the human who just spoke to one of "
                + "them settles it. A manual attachment OVERRIDES an automatic one and is never "
                + "overwritten by a later sweep. The reason is mandatory and is kept in the audit "
                + "trail — a conversation filed against the wrong person is something a later "
                + "reader must be able to understand. "
                + "BOTH SIDES are authorized: only your own call — or one nobody is attributed, "
                + "which is the agency's 'to attribute' queue — may be moved, and the target "
                + "record must be one you may work (M01's agency perimeter for a client, M13's "
                + "assignee-or-supervisor rule for a lead). A call that is not yours answers 404, "
                + "because a 403 would confirm a colleague spoke to somebody; a record that is "
                + "not yours answers 403 TELEPHONY_ENTITY_NOT_YOURS, because you can already see "
                + "the call and 'not found' would send you looking for a record in front of you. "
                + "Requires permission: Telephony.Call.Link.")
            .RequireAuthorization(Permissions.CanLinkTelephonyCall.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid callId, LinkCallRequest request, ISender sender, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(
            new LinkCallCommand(callId, request.EntityType, request.EntityId, request.Reason), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            TelephonyErrors.CallNotFound => Results.NotFound(new { error = result.Error }),
            TelephonyErrors.EntityNotYours =>
                Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}

internal sealed record LinkCallRequest(string EntityType, Guid EntityId, string Reason);
