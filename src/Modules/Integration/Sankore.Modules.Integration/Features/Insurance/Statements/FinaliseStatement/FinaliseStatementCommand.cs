namespace Sankore.Modules.Integration.Features.Insurance.Statements.FinaliseStatement;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Closes a month's bordereau: from here its figures are the ones the institution stands behind.
///
/// <para>
/// <b>A deliberate human act, not a job.</b> The generation runs nightly and is rebuildable; the
/// finalisation is the signature, and after it <c>ApplyTotals</c> refuses to recompute — so a
/// regeneration can no longer restate a month already justified to the insurer. Closing it
/// automatically would hand that decision to a cron expression.
/// </para>
/// </summary>
internal sealed record FinaliseStatementCommand(Guid StatementId)
    : IRequest<Result<StatementDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "InsurerStatement";

    public string ResourceId => StatementId.ToString();
}

internal sealed class FinaliseStatementHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<FinaliseStatementHandler> logger)
    : IRequestHandler<FinaliseStatementCommand, Result<StatementDto>>
{
    public async Task<Result<StatementDto>> Handle(
        FinaliseStatementCommand cmd, CancellationToken ct)
    {
        // The tenant query filter does the isolation: another tenant's statement is simply absent
        // and the endpoint answers NOT_FOUND.
        var statement = await db.InsurerStatements
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Id == cmd.StatementId, ct);

        if (statement is null)
            return Result.Fail<StatementDto>(StatementErrors.NotFound);

        if (!statement.IsRebuildable)
            return Result.Fail<StatementDto>(StatementErrors.AlreadyClosed);

        // The actor comes from ICurrentUser, never from the command: FinalisedBy is the one
        // name a regulator asks about first, and a caller-supplied one is a signature anybody
        // could attribute to a colleague.
        statement.Finalise(currentUser.Id, clock);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Statement {StatementId} ({Period}) finalised by {Actor}: {Commission} {Currency} "
            + "commission on {Collected} collected",
            statement.Id, statement.Period, currentUser.Id,
            statement.CommissionAmount, statement.Currency, statement.PremiumCollected);

        return Result.Ok(StatementDto.From(statement));
    }
}
