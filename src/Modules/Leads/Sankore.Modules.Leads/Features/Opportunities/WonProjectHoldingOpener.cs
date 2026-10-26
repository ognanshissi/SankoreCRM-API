namespace Sankore.Modules.Leads.Features.Opportunities;

using System.Transactions;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Asks M01 for the draft contract a won project owes. Shared by the closing, which swallows a
/// failure (the commercial decision stands whatever M01 answers), and by
/// <c>OpenProjectHolding</c>, which reports it to the agent who asked again.
/// </summary>
/// <remarks>
/// M01's opening is idempotent on the project, so calling this twice returns the contract the
/// first call opened rather than a second one.
/// </remarks>
internal static class WonProjectHoldingOpener
{
    public static async Task<Result<Guid>> OpenAsync(
        Opportunity opportunity,
        Guid clientId,
        ICustomersModule customers,
        IAdministrationModule administration,
        Guid actorId,
        CancellationToken ct)
    {
        // Suppress, twice: both modules read through their own DbContext, and a second connection
        // enlisting in the ambient scope promotes it to a distributed transaction, which Npgsql
        // does not support and which fails at run time.
        using var suppressed = new TransactionScope(
            TransactionScopeOption.Suppress,
            TransactionScopeAsyncFlowOption.Enabled);

        var lineId = opportunity.ProductLineId
            ?? await administration.ResolveProductLineAsync(opportunity.TenantId, opportunity.Product, ct);

        if (lineId is null)
        {
            suppressed.Complete();
            return Result.Fail<Guid>(OpportunityErrors.ProductNotInCatalogue);
        }

        var result = await customers.CreateHoldingFromOpportunityAsync(
            new CreateHoldingFromOpportunityRequest(
                TenantId:            opportunity.TenantId,
                ClientId:            clientId,
                ProductLineId:       lineId.Value,
                SourceOpportunityId: opportunity.Id,
                ClosedByUserId:      actorId),
            ct);

        suppressed.Complete();
        return result;
    }
}
