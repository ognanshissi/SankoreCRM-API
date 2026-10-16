namespace Sankore.Modules.Integration.Features.Telephony.WrapUp;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class WrapUpCallEndpoint
{
    internal static IEndpointRouteBuilder MapWrapUpCall(this IEndpointRouteBuilder app)
    {
        app.MapPost("calls/{callId:guid}/wrap-up", Handle)
            .WithName("WrapUpTelephonyCall")
            .WithSummary("Say how the call went")
            .WithDescription(
                "The outcome must be an ACTIVE code of your tenant's referential — see GET "
                + "telephony/call-outcomes — because two of its columns carry behaviour: one "
                + "decides whether the call stops a lead's first-contact SLA, another makes "
                + "callbackAt mandatory (422 TELEPHONY_CALLBACK_DATE_REQUIRED). "
                + "A call that has not ended is refused (422 TELEPHONY_CALL_NOT_ENDED): the agent "
                + "is still speaking. Only your own call — or one nobody is attributed — may be "
                + "wrapped up; another agent's answers 404, because a 403 would confirm a "
                + "colleague spoke to somebody. "
                + "The note is kept ENCRYPTED and never leaves this module: it is excluded from "
                + "the event M13 consumes and from every timeline summary. "
                + "Wrapping up twice is allowed — an agent may correct an outcome — and M13 is "
                + "idempotent on the call. Audited. "
                + "Requires permission: Telephony.Call.View.")
            .RequireAuthorization(Permissions.CanViewTelephonyCall.Code)
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
        Guid callId, WrapUpCallRequest request, ISender sender, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(
            new WrapUpCallCommand(callId, request.OutcomeCode, request.Note, request.CallbackAt), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            TelephonyErrors.CallNotFound => Results.NotFound(new { error = result.Error }),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}

internal sealed record WrapUpCallRequest(
    string OutcomeCode, string? Note, DateTimeOffset? CallbackAt);
