namespace Sankore.Modules.Integration.Features.Telephony.Missed;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Transactions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Turns a call from an unknown number into a lead (TEL-B-13, the optional half).
///
/// <para>
/// <b>Optional, and on the agent's initiative rather than automatic.</b> Most unanswered calls to
/// a branch are not prospects — wrong numbers, suppliers, somebody's family — and a tenant whose
/// every missed call became a lead would have a pipeline nobody trusts within a week. So there is
/// no job that does this: there is a button, pressed by the agent who decided the call was worth
/// following up.
/// </para>
/// </summary>
internal sealed record CreateLeadFromCallCommand(Guid CallId)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyCall";

    public string? ResourceId => CallId.ToString();
}

internal sealed class CreateLeadFromCallHandler(
    IntegrationDbContext db,
    TelephonyNumberProtector numbers,
    TelephonyCallMatcher matcher,
    ILeadsModule leads,
    ICurrentUser currentUser,
    ILogger<CreateLeadFromCallHandler> logger)
    : IRequestHandler<CreateLeadFromCallCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateLeadFromCallCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var call = await db.TelCalls
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.CallId, ct);

        if (call is null)
            return Result.Fail<Guid>(TelephonyErrors.CallNotFound);

        // Same rule as the wrap-up and the manual attachment: the caller's own call, or one
        // nobody is attributed. A 403 would confirm a colleague spoke to somebody.
        if (call.CrmUserId is { } owner && owner != currentUser.Id)
            return Result.Fail<Guid>(TelephonyErrors.CallNotFound);

        // Already attached: it IS a lead or a client. Creating a second record for the same person
        // is the one thing this button must not do, and the list already hides it for such a call
        // — this is the guard for a screen holding a stale list.
        if (call.EntityId is not null)
            return Result.Fail<Guid>(TelephonyErrors.CallAlreadyAttached);

        if (call.Direction != TelCallDirection.Inbound)
            return Result.Fail<Guid>(TelephonyErrors.NotAnInboundCall);

        var number = numbers.Reveal(call.FromNumberEnc);

        if (string.IsNullOrWhiteSpace(number))
        {
            // A withheld caller id, or an anonymised row. There is nothing to create a lead from
            // and nothing an agent can do about it.
            return Result.Fail<Guid>(TelephonyErrors.NumberUnavailable);
        }

        // Asked again, and not taken from call.EntityId: the journal may have failed to resolve
        // this number because M01 was down at the time, and a CLIENT resolving now is a reason not
        // to create a lead at all. This is the only place that can tell those apart.
        var candidates = await matcher.ResolveCandidatesAsync(currentUser.TenantId, number, ct);

        var client = candidates.FirstOrDefault(c => c.EntityType == TelEntityTypes.Customer);

        if (client is not null)
        {
            // Attach to the client instead of creating a lead, and say so. An agent who pressed
            // "create a lead" for somebody who turns out to be a customer wanted the call filed
            // against them, which is what this does.
            var attachedToClient = call.Attach(
                TelMatchStatus.Matched, TelMatchMethod.Number,
                TelEntityTypes.Customer, client.EntityId);

            if (attachedToClient.IsSuccess) await db.SaveChangesAsync(ct);

            return Result.Fail<Guid>(TelephonyErrors.CallerIsAlreadyAClient);
        }

        // The agency the call belongs to, as far as this module can tell: the agent's own is not
        // knowable here (tel_call carries a user and no agency), so the lead is created without
        // one and M13's dispatching treats that as "no geographic signal" rather than guessing.
        var created = await CaptureAsync(call.Id, number, call.StartedAt, ct);

        if (created.IsFailure)
            return Result.Fail<Guid>(created.Error!);

        var attached = call.Attach(
            TelMatchStatus.Matched, TelMatchMethod.Manual,
            TelEntityTypes.Lead, created.Value.LeadId);

        if (attached.IsFailure)
        {
            // Manual never loses to anything, so the only reachable refusals are shape ones the
            // call row cannot have. Logged rather than swallowed: the lead exists either way and
            // the agent can attach by hand.
            logger.LogWarning(
                "Lead {LeadId} was created from call {CallId} but the call could not be attached "
                + "({Error}).", created.Value.LeadId, call.Id, attached.Error);
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Call {CallId} of tenant {TenantId}: lead {LeadId} {Action} by user {UserId}.",
            call.Id, currentUser.TenantId, created.Value.LeadId,
            created.Value.Created ? "created" : "reused", currentUser.Id);

        return Result.Ok(created.Value.LeadId);
    }

    /// <summary>
    /// Asks M13 to create the lead, with the ambient transaction suppressed.
    ///
    /// <para>
    /// <b>Suppressed, like every cross-module call in a command of this repository</b> —
    /// <c>TransactionBehavior</c> holds a <c>System.Transactions</c> scope and M13 reads and writes
    /// through its own DbContext, so a second connection enlisting would promote the ambient
    /// transaction to a distributed one, which Npgsql does not support.
    /// </para>
    ///
    /// <para>
    /// The consequence is that the lead is committed by M13 before this handler's own save, so a
    /// failure after it leaves a lead with no call attached. That is the cheap direction: a lead an
    /// agent can see and attach by hand, rather than a call pointing at a lead that was rolled
    /// back. The reverse order is not available — M13 owns its own transaction either way.
    /// </para>
    /// </summary>
    private async Task<Result<MissedCallLeadResult>> CaptureAsync(
        Guid callId, string number, DateTimeOffset missedAt, CancellationToken ct)
    {
        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            return await leads.CaptureFromMissedCallAsync(
                currentUser.TenantId, number, currentUser.Id, agencyId: null, missedAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The number is NOT logged.
            logger.LogWarning(
                ex, "Creating a lead from call {CallId} of tenant {TenantId} failed.",
                callId, currentUser.TenantId);

            return Result.Fail<MissedCallLeadResult>(TelephonyErrors.LeadCreationFailed);
        }
    }
}

internal static class CreateLeadFromCallEndpoint
{
    internal static IEndpointRouteBuilder MapCreateLeadFromCall(this IEndpointRouteBuilder app)
    {
        app.MapPost("calls/{callId:guid}/create-lead", Handle)
            .WithName("CreateLeadFromTelephonyCall")
            .WithSummary("Turn a call from an unknown number into a lead")
            .WithDescription(
                "On your initiative, never automatic: most unanswered calls to a branch are not "
                + "prospects, and a tenant whose every missed call became a lead would have a "
                + "pipeline nobody trusts within a week. "
                + "The lead gets a SYNTHETIC name ('Appel manqué du ...') because nobody spoke — "
                + "replace it on your first real contact — and you become its owner. "
                + "Idempotent on the NUMBER: a second call from the same caller answers the "
                + "existing open lead instead of creating a duplicate. "
                + "Answers 409 TELEPHONY_CALLER_IS_ALREADY_A_CLIENT when the number turns out to "
                + "belong to a client (the call is attached to them instead), 409 "
                + "TELEPHONY_CALL_ALREADY_ATTACHED when the journal already resolved the caller, "
                + "and 422 TELEPHONY_NUMBER_UNAVAILABLE for a withheld caller id. Audited. "
                + "Requires permission: Telephony.Call.Link.")
            .RequireAuthorization(Permissions.CanLinkTelephonyCall.Code)
            .Produces<CreateLeadFromCallResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid callId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new CreateLeadFromCallCommand(callId), ct);

        if (result.IsSuccess)
            return Results.Ok(new CreateLeadFromCallResponse(result.Value));

        return result.Error switch
        {
            TelephonyErrors.CallNotFound => Results.NotFound(new { error = result.Error }),
            TelephonyErrors.CallAlreadyAttached
                or TelephonyErrors.CallerIsAlreadyAClient
                => Results.Conflict(new { error = result.Error }),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}

internal sealed record CreateLeadFromCallResponse(Guid LeadId);
