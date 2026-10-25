namespace Sankore.Modules.Leads.Features.Retention;

using System.Transactions;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// Whether the person behind a lead or a client asked to be forgotten and waits for the end of
/// their legal retention (data-purge lot 3): then no new project (prospection) and no template
/// WhatsApp (a contact we initiate). Replying to a message the person sent stays possible — they
/// started it.
/// </summary>
internal static class ProcessingRestriction
{
    /// <summary>A project or a template refused for that reason.</summary>
    public const string Error = "CLIENT_PROCESSING_RESTRICTED";

    public static Task<bool> IsRestrictedAsync(ICustomersModule? customers, Guid tenantId, Lead lead, CancellationToken ct)
        => IsRestrictedAsync(customers, tenantId, lead.ConvertedToCustomerId ?? lead.CustomerId, ct);

    public static async Task<bool> IsRestrictedAsync(ICustomersModule? customers, Guid tenantId, Guid? clientId, CancellationToken ct)
    {
        if (customers is null || clientId is not { } id) return false;

        // M01 reads through its own connection, never enlisted in a caller's transaction.
        using var suppress = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        return await customers.IsProcessingRestrictedAsync(tenantId, id, ct);
    }
}
