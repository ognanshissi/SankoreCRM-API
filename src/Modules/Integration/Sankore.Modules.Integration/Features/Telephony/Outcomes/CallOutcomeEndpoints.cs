namespace Sankore.Modules.Integration.Features.Telephony.Outcomes;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The call-outcome referential's four routes (TEL-B-11).
///
/// <para>
/// The LIST is readable by anyone who may see a call — the wrap-up screen needs it — while the
/// three writes are settings. Gating the list behind <c>Telephony.Settings.Manage</c> would mean
/// no agent could qualify a call without being an administrator.
/// </para>
/// </summary>
internal static class CallOutcomeEndpoints
{
    internal static IEndpointRouteBuilder MapCallOutcomes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("call-outcomes", ListAsync)
            .WithName("ListTelephonyCallOutcomes")
            .WithSummary("The tenant's call outcomes, in display order")
            .WithDescription(
                "What the wrap-up screen offers. Two of the fields carry BEHAVIOUR: "
                + "countsAsContact decides whether the call stops a lead's first-contact SLA, and "
                + "requiresCallbackDate makes callbackAt mandatory on the wrap-up. "
                + "isSystem marks the platform baseline — renameable and deactivatable, never "
                + "deletable, because a call wrapped up last year points at the code. "
                + "Inactive ones are excluded unless includeInactive=true. "
                + "Requires permission: Telephony.Call.View.")
            .RequireAuthorization(Permissions.CanViewTelephonyCall.Code)
            .Produces<IReadOnlyList<CallOutcomeDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("call-outcomes", CreateAsync)
            .WithName("CreateTelephonyCallOutcome")
            .WithSummary("Add an outcome of your own")
            .WithDescription(
                "The code is upper-cased and must be unique in your tenant — including against a "
                + "DEACTIVATED one, which answers 409: re-adding a code somebody switched off is a "
                + "reactivation (POST call-outcomes/{code}/activate), not a creation, and treating "
                + "it as one would silently keep the old row's behaviour flags. Audited. "
                + "Requires permission: Telephony.Settings.Manage.")
            .RequireAuthorization(Permissions.CanManageTelephonySettings.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPut("call-outcomes/{code}", UpdateAsync)
            .WithName("UpdateTelephonyCallOutcome")
            .WithSummary("Rename an outcome and move it in the list")
            .WithDescription(
                "The LABEL and the ORDER only. The two behaviour flags are deliberately not "
                + "editable: countsAsContact decides whether a call stops a lead's SLA, and "
                + "flipping it would retroactively change what every call already wrapped up under "
                + "that code meant — a month of SLA figures recomputed by a settings screen. To "
                + "change behaviour, add an outcome and deactivate the old one, which leaves the "
                + "history saying what it said. Audited. "
                + "Requires permission: Telephony.Settings.Manage.")
            .RequireAuthorization(Permissions.CanManageTelephonySettings.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapPost("call-outcomes/{code}/{state}", SetActiveAsync)
            .WithName("SetTelephonyCallOutcomeActive")
            .WithSummary("Take an outcome out of the wrap-up list, or put it back")
            .WithDescription(
                "{state} is 'activate' or 'deactivate'. NEVER a delete, for a baseline outcome or "
                + "any other: a call wrapped up last year points at the code, and removing it would "
                + "leave that call explained by nothing. A deactivated outcome is refused by the "
                + "wrap-up, so a screen holding a stale list cannot keep using it. Audited. "
                + "Requires permission: Telephony.Settings.Manage.")
            .RequireAuthorization(Permissions.CanManageTelephonySettings.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> ListAsync(
        ISender sender, CancellationToken ct, bool includeInactive = false)
        => Results.Ok((await sender.Send(new ListCallOutcomesQuery(includeInactive), ct)).Value);

    private static async Task<IResult> CreateAsync(
        CreateCallOutcomeRequest request, ISender sender, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(new CreateCallOutcomeCommand(
            request.Code, request.Label, request.CountsAsContact,
            request.RequiresCallbackDate, request.DisplayOrder), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error == TelephonyErrors.OutcomeCodeAlreadyUsed
            ? Results.Conflict(new { error = result.Error })
            : Results.UnprocessableEntity(new { error = result.Error });
    }

    private static async Task<IResult> UpdateAsync(
        string code, UpdateCallOutcomeRequest request, ISender sender, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(
            new UpdateCallOutcomeCommand(code, request.Label, request.DisplayOrder), ct);

        return Answer(result);
    }

    private static async Task<IResult> SetActiveAsync(
        string code, string state, ISender sender, CancellationToken ct)
    {
        // Two literals and nothing else. A boolean in the path would be a route somebody calls
        // with "1" or "True" and gets a 404 from the router rather than an answer.
        var isActive = state switch
        {
            "activate" => true,
            "deactivate" => false,
            _ => (bool?)null,
        };

        if (isActive is null)
            return Results.UnprocessableEntity(new { error = "TELEPHONY_UNKNOWN_OUTCOME_STATE" });

        return Answer(await sender.Send(
            new SetCallOutcomeActiveCommand(code, isActive.Value), ct));
    }

    private static IResult Answer(Result result)
    {
        if (result.IsSuccess) return Results.NoContent();

        return result.Error == TelephonyErrors.OutcomeNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.UnprocessableEntity(new { error = result.Error });
    }
}

internal sealed record CreateCallOutcomeRequest(
    string Code,
    string Label,
    bool CountsAsContact,
    bool RequiresCallbackDate,
    int DisplayOrder);

internal sealed record UpdateCallOutcomeRequest(string Label, int DisplayOrder);
