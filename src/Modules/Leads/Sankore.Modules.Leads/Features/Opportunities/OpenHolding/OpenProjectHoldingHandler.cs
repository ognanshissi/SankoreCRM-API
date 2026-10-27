namespace Sankore.Modules.Leads.Features.Opportunities.OpenHolding;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class OpenProjectHoldingHandler(
    LeadsDbContext db,
    ICustomersModule customers,
    IAdministrationModule administration,
    ICurrentUser currentUser,
    History.ProjectHistoryRecorder? history = null)
    : IRequestHandler<OpenProjectHoldingCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(OpenProjectHoldingCommand cmd, CancellationToken ct)
    {
        var opportunity = await db.Opportunities
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);

        if (opportunity is null)
            return Result.Fail<Guid>(OpportunityErrors.NotFound);

        if (opportunity.Stage != OpportunityStage.ClosedWon)
            return Result.Fail<Guid>(OpportunityErrors.NotWon);

        // The project's own client, else its converted lead's — the same chain as the step gate.
        var clientId = opportunity.CustomerEntityId;
        if (clientId is null && opportunity.LeadId is { } leadId)
        {
            clientId = await db.Leads
                .AsNoTracking()
                .Where(l => l.Id == leadId)
                .Select(l => l.ConvertedToCustomerId ?? l.CustomerId)
                .FirstOrDefaultAsync(ct);
        }

        if (clientId is null)
            return Result.Fail<Guid>(OpportunityErrors.NoClient);

        var opened = await WonProjectHoldingOpener.OpenAsync(
            opportunity, clientId.Value, customers, administration, currentUser.Id, ct);
        if (opened.IsSuccess && history is not null)
            await history.HoldingOpenedAsync(opportunity, opened.Value, ct);
        return opened;
    }
}
