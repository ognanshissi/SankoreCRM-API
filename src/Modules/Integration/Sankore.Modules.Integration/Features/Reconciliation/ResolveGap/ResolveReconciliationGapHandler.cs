namespace Sankore.Modules.Integration.Features.Reconciliation.ResolveGap;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Closes one gap by hand.
///
/// <para>
/// The rule is NOT re-implemented: <c>IntegrationReconciliationGap.Resolve</c> refuses a gap that
/// is not <c>Open</c> and refuses a blank note, and returns a <see cref="Result"/> rather than
/// throwing — so a stale screen offering "resolve" on a gap somebody has already closed, or that
/// last night's comparison closed automatically, gets
/// <see cref="IntegrationErrors.GapAlreadyResolved"/> and a 409 rather than a 500.
/// </para>
/// </summary>
internal sealed class ResolveReconciliationGapHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<ResolveReconciliationGapHandler> logger)
    : IRequestHandler<ResolveReconciliationGapCommand, Result<ResolveReconciliationGapResult>>
{
    public async Task<Result<ResolveReconciliationGapResult>> Handle(
        ResolveReconciliationGapCommand request, CancellationToken ct)
    {
        // The global query filter scopes this to the caller's tenant, so a gap of another tenant
        // is simply absent and the endpoint answers 404 — never 403, the house rule: "forbidden"
        // would confirm that the gap, and therefore the divergence, exists somewhere.
        //
        // AsTracking because the context defaults to NoTracking (module convention) and this one
        // is here to be mutated.
        var gap = await db.ReconciliationGaps
            .AsTracking()
            .FirstOrDefaultAsync(g => g.Id == request.GapId, ct);

        if (gap is null)
            return Result.Fail<ResolveReconciliationGapResult>(IntegrationErrors.GapNotFound);

        var resolved = gap.Resolve(currentUser.Id, request.Note, clock);

        if (resolved.IsFailure)
            return Result.Fail<ResolveReconciliationGapResult>(resolved.Error!);

        await db.SaveChangesAsync(ct);

        // The gap type and the actor, never the note: it is operator prose about a customer's
        // situation and this module keeps that out of its logs, like every other operator-facing
        // string here.
        logger.LogInformation(
            "Reconciliation gap {GapId} ({GapType}) resolved by {UserId}",
            gap.Id, gap.GapType, currentUser.Id);

        return Result.Ok(new ResolveReconciliationGapResult(
            GapId: gap.Id,
            GapType: gap.GapType.ToString(),
            Resolution: gap.Resolution.ToString(),
            ResolvedBy: gap.ResolvedBy,
            ResolvedAt: gap.ResolvedAt));
    }
}
