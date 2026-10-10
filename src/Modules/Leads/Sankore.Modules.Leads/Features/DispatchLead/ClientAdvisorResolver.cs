namespace Sankore.Modules.Leads.Features.DispatchLead;

using System.Transactions;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// The "client history" dispatching criterion of F13.10 (BNF-B-22): a request from a customer
/// already on file goes to that customer's own advisor, when that advisor is available.
///
/// <para>
/// A dedicated service rather than a branch in <c>DispatchLeadHandler</c> for two reasons: the
/// rule is worth testing on its own, and the cross-module read it needs has to suppress the
/// ambient transaction — an idiom that should exist in one place rather than be re-derived at each
/// call site.
/// </para>
/// </summary>
internal sealed class ClientAdvisorResolver(
    ICustomersModule customers,
    ILogger<ClientAdvisorResolver> logger)
{
    /// <summary>
    /// The advisor to route <paramref name="lead"/> to, or null when the criterion does not apply.
    /// </summary>
    /// <param name="eligibleAgents">
    /// Agents who already passed availability, agency and rule-exclusion checks. The advisor is
    /// only returned when they are in this list: "si ce conseiller est disponible" is a condition,
    /// not a preference, and routing to an unavailable advisor would park the lead with somebody
    /// who is not working it.
    /// </param>
    internal async Task<AgentSummary?> ResolveAsync(
        Lead lead, IReadOnlyList<AgentSummary> eligibleAgents, CancellationToken ct)
    {
        if (lead.CustomerId is not { } customerId)
            return null;
        if (eligibleAgents.Count == 0)
            return null;

        Guid? advisorId;

        try
        {
            // TransactionBehavior holds a System.Transactions scope around the dispatch command,
            // and M01 reads through its own DbContext: a second connection enlisting in that scope
            // promotes it to a distributed transaction, which Npgsql does not support and which
            // fails at run time. Suppressed for the duration of the read, as ConvertLeadHandler
            // and CaptureLeadHandler already do.
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            var summary = await customers.GetClientSummaryAsync(lead.TenantId, customerId, ct);
            suppressed.Complete();

            advisorId = summary?.AdvisorUserId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Routing must not stop because M01 is unreachable. The configured strategy still
            // runs, and the lead reaches an agent — just not necessarily the client's own.
            logger.LogWarning(
                ex, "Could not read the advisor of customer {CustomerId}; dispatching normally.",
                customerId);
            return null;
        }

        if (advisorId is not { } advisor)
            return null;

        var match = eligibleAgents.FirstOrDefault(a => a.Id == advisor);

        if (match is null)
        {
            logger.LogInformation(
                "Lead {LeadId} belongs to customer {CustomerId}, whose advisor {AdvisorId} is not "
                + "available for this lead; falling back to the configured strategy.",
                lead.Id, customerId, advisor);
        }

        return match;
    }
}
