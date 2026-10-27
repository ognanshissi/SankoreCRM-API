namespace Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Closes the request, then — only if it was WON — asks M01 to open the draft contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>A synchronous call to M01 and not an event, deliberately.</b> M01 consumes no Leads event
/// and cannot: they are declared in this module's main assembly, which module isolation forbids it
/// to reference. The sanctioned route is <c>ICustomersModule</c>, and the conversion path already
/// takes it — see <c>ConvertLeadHandler.TryOpenHoldingAsync</c>, whose shape this copies down to
/// the suppressed transaction scope.
/// </para>
/// </remarks>
internal sealed class CloseOpportunityHandler(
    LeadsDbContext db,
    ICustomersModule customers,
    IAdministrationModule administration,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<CloseOpportunityHandler> logger,
    ProjectTemplates.ProjectTemplateResolver? templates = null,
    ProjectTemplates.ProjectStepGate? gate = null,
    ProjectTemplates.ProjectLeadConversion? conversion = null)
    : IRequestHandler<CloseOpportunityCommand, Result<CloseOpportunityResult>>
{
    public async Task<Result<CloseOpportunityResult>> Handle(
        CloseOpportunityCommand cmd, CancellationToken ct)
    {
        var opportunity = await db.Opportunities
            .AsTracking()
            .FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);

        if (opportunity is null)
            return Result.Fail<CloseOpportunityResult>(OpportunityErrors.NotFound);

        if (opportunity.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail<CloseOpportunityResult>(LeadConcurrency.ConflictError);

        // Winning reaches every step of the template, the approval one included (lot 3): a project
        // cannot be won around its KYC check by skipping the step that carries it. Losing is
        // always allowed.
        if (cmd.Outcome == OpportunityStage.ClosedWon && templates is not null && gate is not null
            && opportunity.Stage is not (OpportunityStage.ClosedWon or OpportunityStage.ClosedLost))
        {
            var template = await templates.ForProjectAsync(opportunity, ct);
            var current = template.CurrentStep(opportunity.StepId, opportunity.Stage);
            var allowed = await gate.CheckAsync(
                opportunity, template,
                currentIndex: current is null ? -1 : template.Steps.ToList().FindIndex(s => s.Id == current.Id),
                targetIndex:  template.Steps.Count,
                ct);
            if (allowed.IsFailure)
                return Result.Fail<CloseOpportunityResult>(allowed.Error!);
        }

        // Winning approves the project when no earlier step did: the lead converts (product
        // owner, 2026-10-24). Read BEFORE closing — a closed project stands on no step.
        var wonTemplate = cmd.Outcome == OpportunityStage.ClosedWon && conversion is not null && templates is not null
            && opportunity.Stage is not (OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
            ? await templates.ForProjectAsync(opportunity, ct)
            : null;

        var closed = opportunity.Close(cmd.Outcome, clock.GetUtcNow(), cmd.Reason);
        if (closed.IsFailure)
            return Result.Fail<CloseOpportunityResult>(closed.Error!);

        if (wonTemplate is not null)
            await conversion!.ConvertIfApprovedAsync(opportunity, wonTemplate, wonTemplate.Steps.Count, ct);

        await db.SaveChangesAsync(ct);

        // AFTER the save and best-effort, exactly like the conversion: the agent's decision is
        // recorded whatever M01 or M12 answer. A closing rolled back because a catalogue was
        // unreachable would lose a commercial act over an outage in a module that has no say in it.
        var holdingId = cmd.Outcome == OpportunityStage.ClosedWon
            ? await TryOpenHoldingAsync(opportunity, ct)
            : null;

        return Result.Ok(new CloseOpportunityResult(holdingId));
    }

    /// <summary>
    /// The draft contract a won request owes, or null when it owes none.
    ///
    /// <para>
    /// Three conditions, and the last one is the one that fails in practice. The request must be
    /// attached to a CLIENT — one that only carries a lead has no file to open a contract on, and
    /// that is the conversion's job. And its product must RESOLVE onto a catalogue line: the
    /// product is free text (<c>Opportunity.Product</c>), so "Assurance santé" written by hand
    /// resolves to nothing. Refusing the closing instead would make a commercial decision depend on
    /// the spelling of a label; the project page offers « Ouvrir le contrat » instead
    /// (<c>OpenProjectHolding</c>), which retries the same opening and says why it failed.
    /// </para>
    /// </summary>
    private async Task<Guid?> TryOpenHoldingAsync(Opportunity opportunity, CancellationToken ct)
    {
        if (opportunity.CustomerEntityId is not { } clientId)
            return null;

        try
        {
            var result = await WonProjectHoldingOpener.OpenAsync(
                opportunity, clientId, customers, administration, currentUser.Id, ct);

            if (result.IsSuccess)
                return result.Value;

            // HOLDING_ALREADY_HELD lands here and is not an incident: the client already holds a
            // live contract on a line that forbids a second. The request is won either way, and
            // POST opportunities/{id}/holding retries the opening once the cause is fixed.
            logger.LogInformation(
                "Request {OpportunityId} was won but opened no draft holding: {Error}",
                opportunity.Id, result.Error);

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Opening the draft holding for won request {OpportunityId} threw.",
                opportunity.Id);

            return null;
        }
    }
}
