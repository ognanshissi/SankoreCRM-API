namespace Sankore.Modules.Integration.Features.Telephony.Missed;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Closes a missed call's entry in the follow-up list, without calling back (TEL-F-09).
///
/// <para>
/// <b>The reason is mandatory and is kept in the AUDIT row, not on the call.</b> Same decision as
/// the manual attachment next door, and the same argument: a column would hold the last reason
/// only, while a later reader needs the sequence. <c>ICommand</c> is what puts the payload — the
/// reason, the author and the instant together — under the permission that gates reading the
/// audit trail.
/// </para>
///
/// <para>
/// <b>Who may close one: the agent it is attributed to, or anybody when nobody owns it.</b> The
/// agency's "to attribute" queue is worked by the whole branch, which is why the missed list
/// shows it to everyone — so it has to be closable by them too. A colleague's own missed call is
/// theirs, and the refusal is folded into "not found" because a 403 would confirm that somebody
/// tried to reach them.
/// </para>
/// </summary>
internal sealed record DismissMissedCallCommand(Guid CallId, string? Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyCall";

    public string? ResourceId => CallId.ToString();
}

internal sealed class DismissMissedCallValidator : AbstractValidator<DismissMissedCallCommand>
{
    public DismissMissedCallValidator()
    {
        // Mandatory, because the whole value of a closure without a call back is that somebody
        // said why. An entry closed with nothing is indistinguishable from one lost.
        //
        // One WithMessage per rule, and that is not cosmetic: FluentValidation binds WithMessage
        // to the IMMEDIATELY PRECEDING validator, so a single trailing one attached the
        // "a reason is required" sentence to the LENGTH rule — an empty reason answered the
        // generic "'Reason' must not be empty" while a 501-character one explained that a reason
        // is required. Exactly inverted, and invisible until somebody read the two messages.
        RuleFor(x => x.Reason)
            .NotEmpty()
            .WithMessage(
                "A reason is required: closing a missed call without calling back is a decision, "
                + "and one nobody wrote down cannot be reviewed.")
            .MaximumLength(500)
            .WithMessage("A reason is at most 500 characters.");
    }
}

internal sealed class DismissMissedCallHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<DismissMissedCallHandler> logger)
    : IRequestHandler<DismissMissedCallCommand, Result>
{
    public async Task<Result> Handle(DismissMissedCallCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var call = await db.TelCalls
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.CallId, ct);

        if (call is null)
            return Result.Fail(TelephonyErrors.CallNotFound);

        if (call.CrmUserId is { } owner && owner != currentUser.Id)
        {
            logger.LogWarning(
                "User {UserId} of tenant {TenantId} tried to close missed call {CallId}, which "
                + "is attributed to another agent.",
                currentUser.Id, currentUser.TenantId, call.Id);

            return Result.Fail(TelephonyErrors.CallNotFound);
        }

        // The aggregate owns the rule that only a missed or voicemail call can be dismissed, and
        // that a second closure is a success rather than a conflict.
        var dismissed = call.Dismiss(currentUser.Id, clock.GetUtcNow());

        if (dismissed.IsFailure) return dismissed;

        await db.SaveChangesAsync(ct);

        // No reason in the line: it is free text an agent typed about why a person needs no call
        // back, and it belongs in the audit row where reading it is itself gated.
        logger.LogInformation(
            "Missed call {CallId} of tenant {TenantId} closed without a call back by user "
            + "{UserId}.", call.Id, currentUser.TenantId, currentUser.Id);

        return Result.Ok();
    }
}

internal static class DismissMissedCallEndpoint
{
    internal static IEndpointRouteBuilder MapDismissMissedCall(this IEndpointRouteBuilder app)
    {
        app.MapPost("calls/{callId:guid}/dismiss", Handle)
            .WithName("DismissTelephonyMissedCall")
            .WithSummary("Close a missed call without calling back")
            .WithDescription(
                "Takes the entry out of GET telephony/calls/missed when no call back is needed. "
                + "A reason is REQUIRED and is kept in the audit trail rather than on the call, "
                + "because a column would hold the last reason only while a reviewer needs the "
                + "sequence. "
                + "Only a Missed or Voicemail call can be closed (422 "
                + "TELEPHONY_CALL_NOT_MISSED): a conversation that took place has nothing to "
                + "dismiss, and allowing it would be a way to clear a call out of a colleague's "
                + "queue without qualifying it. "
                + "Wrapping up is the OTHER way out of the list and means something different — "
                + "it describes a conversation, creates a lead activity and may stop an SLA "
                + "clock. Do not use an outcome code to mean 'nothing'. "
                + "Your own call, or one nobody is attributed; a colleague's answers 404. "
                + "Closing twice is a success. Audited. "
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
        Guid callId, DismissMissedCallRequest request, ISender sender, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(new DismissMissedCallCommand(callId, request.Reason), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error == TelephonyErrors.CallNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.UnprocessableEntity(new { error = result.Error });
    }
}

/// <param name="Reason">
/// Why this missed call needs no call back. Required, and kept in the audit trail.
/// </param>
internal sealed record DismissMissedCallRequest(string? Reason);
