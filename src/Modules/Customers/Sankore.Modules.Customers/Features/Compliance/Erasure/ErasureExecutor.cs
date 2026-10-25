namespace Sankore.Modules.Customers.Features.Compliance.Erasure;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Runs an erasure the eligibility allowed — on approval, or the day a restricted one becomes
/// possible. The caller saves (the request is completed in the same save as a client's erasure).
///
/// <para>
/// <b>A client</b> (or a lead of a client) is anonymised here, and <c>ClientAnonymizedEvent</c> —
/// published in the same transaction — makes Leads (its leads, threads, appointments), KYC (its
/// evidence) and Integration (its calls and recordings) erase their copies. <b>A prospect</b> is
/// erased by Leads, which publishes <c>LeadErasedEvent</c> for its calls.
/// </para>
/// </summary>
internal sealed class ErasureExecutor(
    CustomersDbContext db,
    [FromKeyedServices(nameof(CustomersDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    ILogger<ErasureExecutor> logger,
    ILeadsModule? leads = null)
{
    public async Task ExecuteAsync(ErasureRequest request, ErasureVerdict verdict, Guid actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        if (verdict.ClientId is { } clientId)
        {
            // Contact points too: Client.Anonymize closes every active one.
            var client = await db.Clients
                .IgnoreQueryFilters()
                .AsTracking()
                .Include(c => c.ContactPoints)
                .FirstAsync(c => c.TenantId == request.TenantId && c.Id == clientId, ct);

            if (!client.IsAnonymized)
            {
                client.Anonymize(actor, now);
                await publisher.PublishAsync(new ClientAnonymizedEvent(request.TenantId, client.Id, now), ct);
            }
        }
        else
        {
            if (leads is null) throw new InvalidOperationException("Erasing a lead needs the Leads module.");

            // Leads' own transaction, committed before this request is marked: a request completed
            // whose lead is intact would be the one outcome nobody would ever come back to fix. The
            // reverse — erased, request still pending — is retried safely (EraseLeadAsync is idempotent).
            using var suppress = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            await leads.EraseLeadAsync(request.TenantId, request.SubjectId, ct);
        }

        logger.LogWarning(
            "Erasure request {RequestId} of tenant {TenantId}: {SubjectType} {Reference} erased.",
            request.Id, request.TenantId, request.SubjectType, verdict.Reference);
    }
}
