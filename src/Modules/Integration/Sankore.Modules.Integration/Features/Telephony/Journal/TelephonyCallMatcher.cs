namespace Sankore.Modules.Integration.Features.Telephony.Journal;

using System.Transactions;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Leads.PublicApi;

/// <summary>
/// Turns a telephone number into the record a call is about (TEL-B-09).
///
/// <para>
/// <b>It ASKS the other modules and never computes an index itself.</b> Blind-index keys are per
/// module by design — M01's <c>BlindIndexPurpose.Phone</c> and M13's <c>Leads:BlindIndexKey</c>
/// are different hashes of the same number — so an index computed here would match nothing,
/// silently: a lookup that finds no row is indistinguishable from a number nobody called from.
/// Both contracts take the number in clear and answer identifiers, which is the only arrangement
/// a reverse lookup across a module boundary can have.
/// </para>
///
/// <para>
/// <b>A client wins over a lead.</b> Not a preference: a converted lead is excluded by M13's own
/// query, so a number answering on both sides means a client record AND an open request from
/// somebody already on file. The client record is where the conversation belongs, and it carries
/// the request anyway (<c>GetOpenLeadsForCustomerAsync</c>).
/// </para>
///
/// <para>
/// <b>No agency perimeter here, and that is deliberate.</b> This runs under SYSTEM from a webhook,
/// which has no perimeter to apply; narrowing would depend on whose nightly job happened to run.
/// The perimeter is applied on the READ — the screen-pop context of TEL-B-10 — where there is a
/// caller to apply it for. What is stored is the resolution; what is shown is filtered.
/// </para>
/// </summary>
internal sealed class TelephonyCallMatcher(
    ICustomersModule customers,
    ILeadsModule leads,
    ILogger<TelephonyCallMatcher> logger)
{
    /// <summary>
    /// Who the number belongs to.
    ///
    /// <para>
    /// <b>Every cross-module read is wrapped in <c>TransactionScopeOption.Suppress</c>.</b>
    /// <c>TransactionBehavior</c> holds a <c>System.Transactions</c> scope and the other module
    /// reads through its own DbContext; a second connection enlisting promotes the ambient
    /// transaction to a distributed one, which Npgsql does not support and which fails at run
    /// time. The repo precedent is <c>CaptureLeadHandler.TryMatchCustomerAsync</c>, and this
    /// matcher runs inside a command for the same reason it runs inside a webhook handler.
    /// </para>
    ///
    /// <para>
    /// <b>An outage answers <see cref="TelMatchStatus.Unknown"/>, never an exception.</b> A call
    /// nobody could resolve is still a call to journal, and losing the journal because M01 was
    /// down would lose the one record of a conversation that actually happened. The failure is
    /// logged and the catch-up sweep gets a second chance at it, because an unattached call is
    /// re-matched on its next reading.
    /// </para>
    /// </summary>
    internal async Task<TelephonyMatch> ResolveAsync(
        Guid tenantId, string? remoteNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remoteNumber))
            return TelephonyMatch.None;

        var clients = await SafeAsync(
            () => customers.FindMatchesAsync(tenantId, remoteNumber, null, null, ct),
            "M01 client lookup", tenantId);

        if (clients.Count > 1)
            return TelephonyMatch.Ambiguous;

        if (clients.Count == 1)
        {
            return new TelephonyMatch(
                TelMatchStatus.Matched, TelEntityTypes.Customer, clients[0].ClientId);
        }

        var openLeads = await SafeAsync(
            () => leads.FindOpenLeadsByPhoneAsync(tenantId, remoteNumber, ct),
            "M13 lead lookup", tenantId);

        if (openLeads.Count > 1)
            return TelephonyMatch.Ambiguous;

        if (openLeads.Count == 1)
        {
            return new TelephonyMatch(
                TelMatchStatus.Matched, TelEntityTypes.Lead, openLeads[0].LeadId);
        }

        return TelephonyMatch.None;
    }

    private async Task<IReadOnlyList<T>> SafeAsync<T>(
        Func<Task<IReadOnlyList<T>>> read, string what, Guid tenantId)
    {
        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The number is NOT logged: it is the one piece of personal data this method handles,
            // and §9 of the specification forbids it reaching a log at all.
            logger.LogWarning(
                ex, "Telephony attachment: {What} failed for tenant {TenantId}; the call is "
                  + "journalled unattached.", what, tenantId);

            return [];
        }
    }
}

/// <summary>
/// The matcher's verdict. The entity pair is all-or-nothing, which <c>TelCall.Attach</c> and a
/// check constraint both enforce — carried as one record so no caller can set half of it.
/// </summary>
internal sealed record TelephonyMatch(TelMatchStatus Status, string? EntityType, Guid? EntityId)
{
    /// <summary>Nothing matched. An ordinary answer: most inbound calls are from nobody on file.</summary>
    internal static readonly TelephonyMatch None = new(TelMatchStatus.Unknown, null, null);

    /// <summary>
    /// Several records share the number. Left for a human: a family handset or a shop line is
    /// ordinary here, and guessing files a conversation against the wrong person.
    /// </summary>
    internal static readonly TelephonyMatch Ambiguous = new(TelMatchStatus.Ambiguous, null, null);
}
