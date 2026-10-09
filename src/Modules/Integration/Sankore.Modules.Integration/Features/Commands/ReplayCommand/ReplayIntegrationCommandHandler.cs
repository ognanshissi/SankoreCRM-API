namespace Sankore.Modules.Integration.Features.Commands.ReplayCommand;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Puts a rejected command back in the queue. Status and attempt counter only — the payload is
/// not touched here by anyone.
///
/// <para>
/// The replayability rule is NOT re-implemented: <c>IntegrationCommand.Replay</c> goes through
/// the transition table, which allows <c>Rejected → Pending</c> and nothing else. What this
/// handler adds is asking FIRST, through <c>CanTransitionTo</c>, so a stale screen offering
/// "replay" on a command somebody has already replayed gets
/// <see cref="IntegrationErrors.CommandNotReplayable"/> and a 409, rather than the aggregate's
/// <c>DomainException</c> surfacing as a 500. An out-of-table move is a programming error and
/// must throw; a human clicking a button twice is not.
/// </para>
///
/// <para>
/// This handler therefore has no payload protector and needs none. A rejection caused by a wrong
/// VALUE is fixed in the CRM, and the dispatcher re-derives the payload from current CRM state
/// when it sends — see <c>ExecuteIntegrationCommandHandler</c>. A rejection caused by our
/// configuration (a missing mapping, a refused credential) is fixed in the configuration and the
/// very same bytes are re-sent. Neither case wants an operator-supplied payload, and
/// <see cref="ReplayIntegrationCommandCommand"/> explains at length why accepting one would be
/// an unauditable financial write.
/// </para>
/// </summary>
internal sealed class ReplayIntegrationCommandHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<ReplayIntegrationCommandHandler> logger)
    : IRequestHandler<ReplayIntegrationCommandCommand, Result<ReplayIntegrationCommandResult>>
{
    public async Task<Result<ReplayIntegrationCommandResult>> Handle(
        ReplayIntegrationCommandCommand request, CancellationToken ct)
    {
        // The global query filter scopes this to the caller's tenant, so a command of another
        // tenant is simply absent and the endpoint answers 404 — never 403, which would confirm
        // that the command exists somewhere.
        var command = await db.Commands
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == request.CommandId, ct);

        if (command is null)
            return Result.Fail<ReplayIntegrationCommandResult>(IntegrationErrors.CommandNotFound);

        if (!command.CanTransitionTo(CommandStatus.Pending))
            return Result.Fail<ReplayIntegrationCommandResult>(IntegrationErrors.CommandNotReplayable);

        command.Replay(clock);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Command {CommandId} replayed by {UserId}", command.Id, currentUser.Id);

        return Result.Ok(new ReplayIntegrationCommandResult(
            CommandId: command.Id,
            Status: command.Status.ToString(),
            Attempts: command.Attempts,
            PayloadFieldNames: FieldNames(command)));
    }

    /// <summary>
    /// The stored list, split back out. Names only — the values are in a column this handler
    /// never reads and no endpoint returns.
    /// </summary>
    private static IReadOnlyList<string> FieldNames(IntegrationCommand command)
        => string.IsNullOrWhiteSpace(command.PayloadFieldNames)
            ? []
            : command.PayloadFieldNames.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                   | StringSplitOptions.TrimEntries);
}
