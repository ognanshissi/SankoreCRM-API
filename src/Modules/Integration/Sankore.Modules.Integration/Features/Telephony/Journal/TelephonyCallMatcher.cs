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
        var candidates = await ResolveCandidatesAsync(tenantId, remoteNumber, ct);

        if (candidates.Count == 0) return TelephonyMatch.None;
        if (candidates.Count > 1) return TelephonyMatch.Ambiguous;

        var only = candidates[0];

        return new TelephonyMatch(TelMatchStatus.Matched, only.EntityType, only.EntityId);
    }

    /// <summary>
    /// Everyone the number resolves to, with the names and the agencies a screen needs
    /// (TEL-B-10).
    ///
    /// <para>
    /// The same lookup <see cref="ResolveAsync"/> reduces to a verdict, so the two cannot
    /// disagree about who a number belongs to — which they would the day one of them learned
    /// about a new kind of record and the other did not. The journal stores the verdict; the
    /// screen-pop read needs the list, because <c>Ambiguous</c> is only useful to an agent who
    /// can see the two names it is between.
    /// </para>
    ///
    /// <para>
    /// <b>A client found makes the leads irrelevant, and the order is not an optimisation.</b> A
    /// converted lead is excluded by M13's own query, so a number answering on both sides means a
    /// client record AND an open request from somebody already on file — one human, listed once,
    /// as the client they are.
    /// </para>
    ///
    /// <para>
    /// <b>No perimeter here.</b> The caller applies it: this method is also reached from the
    /// SYSTEM webhook path, which has no perimeter, and narrowing in here would make what is
    /// STORED depend on whose job ran.
    /// </para>
    /// </summary>
    internal async Task<IReadOnlyList<ResolvedCandidate>> ResolveCandidatesAsync(
        Guid tenantId, string? remoteNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remoteNumber)) return [];

        var clients = await SafeAsync(
            () => customers.FindMatchesAsync(tenantId, remoteNumber, null, null, ct),
            "M01 client lookup", tenantId);

        if (clients.Count > 0)
        {
            return [.. clients.Select(c => new ResolvedCandidate(
                TelEntityTypes.Customer, c.ClientId, c.DisplayName, c.AgencyId))];
        }

        var openLeads = await SafeAsync(
            () => leads.FindOpenLeadsByPhoneAsync(tenantId, remoteNumber, ct),
            "M13 lead lookup", tenantId);

        return [.. openLeads.Select(l => new ResolvedCandidate(
            TelEntityTypes.Lead, l.LeadId, l.FullName, l.AgencyId))];
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
/// One record a number resolves to, as the owning module describes it.
/// </summary>
/// <param name="AgencyId">
/// Null for a lead that carries none, which M13 documents as ordinary. The caller must read null
/// as "outside every restricted perimeter" rather than "inside all of them" — the opposite
/// reading would show an unassigned lead's name to every agent of the tenant.
/// </param>
internal sealed record ResolvedCandidate(
    string EntityType, Guid EntityId, string DisplayName, Guid? AgencyId);

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
