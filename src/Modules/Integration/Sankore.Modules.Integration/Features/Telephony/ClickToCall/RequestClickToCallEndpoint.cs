namespace Sankore.Modules.Integration.Features.Telephony.ClickToCall;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class RequestClickToCallEndpoint
{
    internal static IEndpointRouteBuilder MapRequestClickToCall(this IEndpointRouteBuilder app)
    {
        app.MapPost("calls/click-to-call", Handle)
            .WithName("RequestTelephonyClickToCall")
            .WithSummary("Ring my handset, then dial one of this record's numbers")
            .WithDescription(
                "Takes a RECORD and a SLOT — entityType (Lead | Customer), entityId, slot "
                + "(Primary | Secondary) — and never a telephone number: the number is read on "
                + "the server by the module that owns it, which also applies its own perimeter. "
                + "An agency perimeter refusal therefore reads as the owning module's own code "
                + "(CLIENT_NOT_FOUND, LEAD_NOT_YOURS). "
                + "SYNCHRONOUS and never retried: the agent is waiting for their own telephone, "
                + "and a retried call rings twice in somebody's hand — re-click instead. "
                + "Answers 422 TELEPHONY_USER_NOT_LINKED when the caller has no active provider "
                + "account (link one under telephony/user-links), 422 TELEPHONY_DISABLED without "
                + "an active telephony connection, 422 UNKNOWN_SENSITIVE_FIELD or "
                + "LEAD_PHONE_SLOT_EMPTY when the slot holds no number, 502 "
                + "TELEPHONY_PROVIDER_REFUSED when the provider said no, and 503 "
                + "TELEPHONY_NUMBER_UNAVAILABLE when the owning module could not be asked. "
                + "The response carries the request id; follow it with GET "
                + "telephony/calls/{callId}/context once the call exists. Audited. "
                + "Requires permission: Telephony.Call.Make.")
            .RequireAuthorization(Permissions.CanMakeTelephonyCall.Code)
            .Produces<RequestClickToCallResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        RequestClickToCallRequest request,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(
            new RequestClickToCallCommand(request.EntityType, request.EntityId, request.Slot), ct);

        // 202 and not 200: the provider has accepted the request and the agent's handset is about
        // to ring. Nothing about the CALL is known yet — there is no call — and a 200 would invite
        // a front end to treat the answer as the conversation having started.
        if (result.IsSuccess)
            return Results.Accepted(value: new RequestClickToCallResponse(result.Value));

        return result.Error switch
        {
            // The provider answered and said no. A gateway failure, not the caller's fault — and
            // a 422 here would have the screen tell the agent to correct something of theirs.
            TelephonyErrors.ProviderRefused =>
                Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway),

            // We could not ask. Retryable, so it must not look like a configuration error.
            TelephonyErrors.NumberUnavailable =>
                Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status503ServiceUnavailable),

            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}

/// <summary>
/// A record and a slot. <b>There is no number field and there must never be one</b> — see
/// <c>DialableNumberResolver</c>.
/// </summary>
internal sealed record RequestClickToCallRequest(
    string EntityType,
    Guid EntityId,
    TelephonyPhoneSlot Slot);

/// <param name="RequestId">
/// The click-to-call request. The screen follows it until the agent's handset rings; it is NOT a
/// call id, because at this instant there is no call.
/// </param>
internal sealed record RequestClickToCallResponse(Guid RequestId);
