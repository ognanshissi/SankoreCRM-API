namespace Sankore.Modules.Integration.Features.Commands.CancelCommand;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// An operator abandons a write (INT-05, criterion 5).
///
/// <para>
/// Final, and available only from <c>Pending</c> and <c>Rejected</c> — the transition table has
/// no edge out of <c>Succeeded</c>, which is the point: a write the external system has confirmed
/// cannot be un-confirmed by a click in SANKORE, and a cancellation that pretended otherwise
/// would leave the two systems disagreeing with no trace of why.
/// </para>
///
/// <para>
/// Same permission as a replay (<c>Integration.Command.Replay</c>) and audited with its author
/// for the same reason: both decide whether a customer's account is ever opened.
/// </para>
/// </summary>
internal sealed record CancelIntegrationCommandCommand(Guid CommandId, string? Reason = null)
    : IRequest<Result<CancelIntegrationCommandResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationCommand";

    public string? ResourceId => CommandId.ToString();
}

internal sealed record CancelIntegrationCommandResult(Guid CommandId, string Status);

internal sealed class CancelIntegrationCommandHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<CancelIntegrationCommandHandler> logger)
    : IRequestHandler<CancelIntegrationCommandCommand, Result<CancelIntegrationCommandResult>>
{
    public async Task<Result<CancelIntegrationCommandResult>> Handle(
        CancelIntegrationCommandCommand request, CancellationToken ct)
    {
        var command = await db.Commands
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == request.CommandId, ct);

        if (command is null)
            return Result.Fail<CancelIntegrationCommandResult>(IntegrationErrors.CommandNotFound);

        // Asked before it is done, for the same reason as the replay: a command already in flight
        // (Sending), already succeeded, or already cancelled is a race or a stale screen, not a
        // programming error — so it is a 409 carrying a code, not the aggregate's exception.
        if (!command.CanTransitionTo(CommandStatus.Cancelled))
            return Result.Fail<CancelIntegrationCommandResult>(IntegrationErrors.CommandNotCancellable);

        command.Cancel(clock);
        await db.SaveChangesAsync(ct);

        // The reason is not stored on the aggregate: LastErrorMessage describes what the external
        // system or our configuration did, and overwriting it with a human's motive would destroy
        // the diagnosis the rejection queue was showing. The audit row — written by
        // AuditBehavior from this very command, author included — is where the motive belongs.
        logger.LogInformation(
            "Command {CommandId} cancelled by {UserId}: {Reason}",
            command.Id, currentUser.Id, request.Reason ?? "(no reason given)");

        return Result.Ok(new CancelIntegrationCommandResult(command.Id, command.Status.ToString()));
    }
}
