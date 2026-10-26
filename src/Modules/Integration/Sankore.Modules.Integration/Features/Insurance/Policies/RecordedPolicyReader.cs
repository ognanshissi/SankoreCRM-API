namespace Sankore.Modules.Integration.Features.Insurance.Policies;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// A client's recorded policies (<see cref="PolicyRecord"/>), with their insurer, their product
/// and the CRM contract they realise. One body for the policies endpoint and for
/// <see cref="IInsuranceGateway.ListRecordedPoliciesAsync"/>, which the client's contract list
/// reads — two copies would be two answers to « which contract is this policy ».
/// </summary>
/// <remarks>
/// Tenant-scoped by the context's global filter, like every read of this module. The perimeter
/// is the CALLER's business: the endpoint checks it, and M01 has already checked it before asking.
/// </remarks>
internal static class RecordedPolicyReader
{
    public static async Task<IReadOnlyList<RecordedPolicy>> ReadAsync(
        IntegrationDbContext db, Guid crmCustomerId, CancellationToken ct)
    {
        var rows = await db.Policies
            .Where(p => p.CrmCustomerId == crmCustomerId)
            .Join(db.Connections, p => p.ConnectionId, c => c.Id, (p, c) => new { Policy = p, Insurer = c.Name })
            .OrderByDescending(x => x.Policy.EffectiveDate)
            .ToListAsync(ct);

        var productIds = rows.Select(r => r.Policy.ProductId).OfType<Guid>().Distinct().ToList();
        var products = await db.InsuranceProducts
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        // In memory: IsPremiumOutstanding is computed on the saga, and a client has a handful.
        var subscriptions = (await db.InsuranceSubscriptions
                .Where(s => s.CrmCustomerId == crmCustomerId && s.PolicyId != null)
                .ToListAsync(ct))
            .ToDictionary(s => s.PolicyId!.Value);

        return [.. rows.Select(r =>
        {
            subscriptions.TryGetValue(r.Policy.Id, out var subscription);

            return new RecordedPolicy(
                PolicyId: r.Policy.Id,
                PolicyNumber: r.Policy.PolicyNumber,
                Insurer: r.Insurer,
                ProductId: r.Policy.ProductId,
                ProductName: r.Policy.ProductId is { } id && products.TryGetValue(id, out var name)
                    ? name
                    : r.Policy.InsurerProductCode,
                Status: r.Policy.Status,
                StatusDetail: r.Policy.StatusDetail,
                EffectiveDate: r.Policy.EffectiveDate,
                ExpiryDate: r.Policy.ExpiryDate,
                NextDueDate: r.Policy.NextDueDate,
                PremiumAmount: r.Policy.PremiumAmount,
                Currency: r.Policy.Currency,
                Periodicity: r.Policy.Periodicity,
                InsuredAmount: r.Policy.InsuredAmount,
                HoldingId: subscription?.CrmHoldingId,
                LastSyncedAt: r.Policy.LastSyncedAt,
                // Issued is not in force: the contract starts once the premium is paid, and the
                // insurer's own status says nothing about that until its next synchronisation.
                PremiumOutstanding: subscription?.IsPremiumOutstanding ?? false);
        })];
    }
}
