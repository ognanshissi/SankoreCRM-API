namespace Sankore.Modules.Integration.Features.Insurance.Claims.ClaimSync;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Ports;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// ASS-09, third and fourth criteria: follows the open dossiers at each insurer, announces a status
/// change ONCE, and tells the declaring agent when the insurer is waiting on documents.
///
/// <para>
/// <b>Driven by OUR open worklist, not by the portfolio.</b> The sweep enumerates the claims this
/// platform holds in a non-terminal state — <c>ix_ins_claim_tenant_status</c> serves it — and
/// reads each distinct policy once. That is a far smaller set than every policy of every insurer,
/// and it has the right shape: a dossier stops costing a nightly read the moment it is settled.
/// The insurer's answer covers the whole policy, so a claim declared at its own counter on a
/// policy we are already following is picked up as a side effect; discovering one on a policy with
/// no open dossier of ours is deliberately NOT done, since it would mean sweeping the entire
/// portfolio to find the rare case.
/// </para>
///
/// <para>
/// <b>The event is published only when the status MOVED</b>, which is <c>ApplySync</c>'s return
/// value — the same contract as ASS-07's policy sync and for the same reason: a nightly sweep
/// would otherwise publish one event per dossier per night, and M08 turns each into a message to
/// the customer.
/// </para>
///
/// <para>
/// <b>The agent task is raised on a DIFFERENT condition from the event</b>, and the asymmetry is
/// the point. A status change is news for the customer; a request for documents is work for the
/// agent, and an insurer that asks for a second document while the dossier is already
/// <c>DocumentsRequired</c> moves no status at all. So the notification fires when the status
/// moved into <c>DocumentsRequired</c> OR when the LIST of missing documents changed — otherwise
/// the second request is invisible and the dossier stalls on a pièce nobody was told about.
/// </para>
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type.
public sealed class SyncClaimsJob(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncClaimsJob> logger)
{
    /// <summary>
    /// Key of the seeded template M08 renders for the agent. Internal so the seeder and this job
    /// cannot drift — an unknown key is a failed send, not an exception, so a typo here would be
    /// an agent task that silently never arrives.
    /// </summary>
    internal const string DocumentsRequiredTemplate = "integration.claims.documents_required";

    /// <summary>Open dossiers per page, and therefore per SQL round trip.</summary>
    private const int ClaimPageSize = 200;

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        // SYSTEM identity, established BEFORE the DI scope: ITenantContext and ICurrentUser are
        // built from it and a Hangfire job has no HTTP context.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IntegrationDbContext>();
        var resolver = sp.GetRequiredService<IntegrationAdapterResolver>();
        var publisher = sp.GetRequiredService<IEventPublisher>();
        var administration = sp.GetRequiredService<IAdministrationModule>();
        var clock = sp.GetRequiredService<TimeProvider>();

        // IgnoreQueryFilters with an explicit tenant predicate: a Hangfire job runs outside any
        // HTTP context, so the ambient tenant cannot be trusted to be the one being processed.
        var connections = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.Family == IntegrationFamily.Insurance
                     && c.IsActive)
            .ToDictionaryAsync(c => c.Id, ct);

        if (connections.Count == 0) return;

        var seen = 0;
        var moved = 0;
        var notified = 0;
        var page = 0;

        while (true)
        {
            // The open dossiers and the insurer-side policy id each needs, in one query. Ordered
            // by id so the pages are stable while the sweep writes.
            var open = await db.Claims
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId
                         && c.Status != ClaimStatus.Refused
                         && c.Status != ClaimStatus.Indemnified)
                .Join(db.Policies.IgnoreQueryFilters(),
                      c => c.PolicyId, p => p.Id,
                      (c, p) => new { c.Id, c.ConnectionId, p.ExternalPolicyId })
                .OrderBy(x => x.Id)
                .Skip(page * ClaimPageSize)
                .Take(ClaimPageSize)
                .ToListAsync(ct);

            if (open.Count == 0) break;

            // One read per POLICY, not per dossier: the insurer answers for the whole policy, and
            // two open claims on one contract must not cost two calls.
            var byPolicy = open
                .GroupBy(x => (x.ConnectionId, x.ExternalPolicyId))
                .ToList();

            foreach (var group in byPolicy)
            {
                if (!connections.TryGetValue(group.Key.ConnectionId, out var connection))
                {
                    // The insurer's connection was switched off while a dossier was open. Not
                    // this job's to fix, and not a reason to abandon the rest.
                    logger.LogDebug(
                        "Claim sync skipped policy {ExternalPolicyId}: connection "
                        + "{ConnectionId} is not an active insurance connection",
                        group.Key.ExternalPolicyId, group.Key.ConnectionId);
                    continue;
                }

                var port = ResolveClaimPort(resolver, connection);
                if (port is null) continue;

                var claims = await port.GetClaimsAsync(new ExternalId(group.Key.ExternalPolicyId), ct);

                if (claims.IsFailure)
                {
                    // One policy's read failing must not abandon the rest: tomorrow's run reads it
                    // again, and a sweep that stops at the first timeout never finishes a night.
                    logger.LogWarning(
                        "Claim sync could not read policy {ExternalPolicyId} at connection "
                        + "{ConnectionId}: {Error}",
                        group.Key.ExternalPolicyId, connection.Id, claims.Code);
                    continue;
                }

                foreach (var incoming in claims.Value)
                {
                    seen++;

                    var outcome = await ApplyAsync(
                        db, publisher, administration, clock, connection, tenantId, incoming, ct);

                    if (outcome.Moved) moved++;
                    if (outcome.Notified) notified++;
                }
            }

            // Saved per page: an interrupted night keeps the pages it finished rather than
            // discarding the whole sweep.
            await db.SaveChangesAsync(ct);

            if (open.Count < ClaimPageSize) break;
            page++;
        }

        if (seen > 0)
        {
            logger.LogInformation(
                "Claim sync for tenant {TenantId}: {Seen} dossier(s) read, {Moved} status "
                + "change(s) announced, {Notified} agent task(s) raised",
                tenantId, seen, moved, notified);
        }
    }

    /// <summary>
    /// Upserts one dossier and answers what it caused. Matched on the insurer's own claim id,
    /// which <c>ux_ins_claim_external</c> makes unique per connection.
    /// </summary>
    private async Task<(bool Moved, bool Notified)> ApplyAsync(
        IntegrationDbContext db,
        IEventPublisher publisher,
        IAdministrationModule administration,
        TimeProvider clock,
        IntegrationConnection connection,
        Guid tenantId,
        InsuranceClaim incoming,
        CancellationToken ct)
    {
        var claim = await db.Claims
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(
                c => c.TenantId == tenantId
                  && c.ConnectionId == connection.Id
                  && c.ExternalClaimId == incoming.ClaimId.Value,
                ct);

        // A dossier we declared whose acknowledgement has not landed yet carries no external id,
        // so it cannot be matched on one. Fall back to the declaration's own triple — the policy,
        // the date and the nature — which is exactly what ux_ins_claim_declaration keys on, and
        // record the reference while we are here: the sync has just learnt it.
        if (claim is null)
        {
            claim = await db.Claims
                .IgnoreQueryFilters()
                .AsTracking()
                .Where(c => c.TenantId == tenantId
                         && c.ConnectionId == connection.Id
                         && c.ExternalClaimId == null
                         && c.OccurredOn == incoming.OccurredOn)
                .Join(db.Policies.IgnoreQueryFilters(),
                      c => c.PolicyId, p => p.Id, (c, p) => new { Claim = c, p.ExternalPolicyId })
                .Where(x => x.ExternalPolicyId == incoming.PolicyId.Value)
                .Select(x => x.Claim)
                .FirstOrDefaultAsync(ct);

            claim?.RecordInsurerReference(
                incoming.ClaimId.Value, incoming.ClaimNumber, clock);
        }

        if (claim is null)
        {
            // A claim declared at the insurer's own counter, on a policy we are following for
            // another dossier. Recorded so the customer's file is complete — and NO event: a
            // dossier discovered three weeks after the fact has not just changed state, and
            // announcing one would tell the customer their claim was accepted today.
            //
            // Not created either when we cannot place it on a policy we hold: the row needs a
            // policy id, and inventing one would attach the loss to the wrong contract.
            var policyId = await db.Policies
                .IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId
                         && p.ConnectionId == connection.Id
                         && p.ExternalPolicyId == incoming.PolicyId.Value)
                .Select(p => new { p.Id, p.CrmCustomerId })
                .FirstOrDefaultAsync(ct);

            if (policyId is null) return (false, false);

            var discovered = ClaimRecord.Create(
                tenantId: tenantId,
                connectionId: connection.Id,
                policyId: policyId.Id,
                crmCustomerId: policyId.CrmCustomerId,
                occurredOn: incoming.OccurredOn,
                nature: string.IsNullOrWhiteSpace(incoming.ClaimNumber)
                    ? "Déclaré chez l'assureur"
                    : incoming.ClaimNumber,
                // SYSTEM: nobody at this institution declared it, and attributing it to an agent
                // would put a name against an act they did not perform.
                declaredBy: Guid.Empty,
                clock: clock);

            discovered.RecordInsurerReference(incoming.ClaimId.Value, incoming.ClaimNumber, clock);

            discovered.ApplySync(
                status: incoming.Status,
                statusDetail: null,
                missingDocuments: incoming.MissingDocuments,
                clock: clock,
                indemnityAmount: incoming.IndemnityAmount);

            db.Claims.Add(discovered);

            return (false, false);
        }

        var previous = claim.Status;

        // Captured BEFORE the sync overwrites it. A changed list is the fourth criterion's second
        // trigger, and ApplySync does not report it.
        var previousMissing = claim.MissingDocuments;

        var statusMoved = claim.ApplySync(
            status: incoming.Status,
            statusDetail: null,
            missingDocuments: incoming.MissingDocuments,
            clock: clock,
            indemnityAmount: incoming.IndemnityAmount);

        if (statusMoved)
        {
            // Through the outbox, in the same transaction as the row that moved: an event without
            // its row would tell a customer about a change this platform does not believe.
            await publisher.PublishAsync(
                new ClaimStatusChangedEvent(
                    TenantId: tenantId,
                    CrmCustomerId: claim.CrmCustomerId,
                    ExternalClaimId: claim.ExternalClaimId!,
                    PreviousStatus: previous.ToString(),
                    CurrentStatus: incoming.Status.ToString(),
                    MissingDocuments: claim.MissingDocuments,
                    ChangedAt: clock.GetUtcNow()),
                ct);
        }

        var notified = await RaiseDocumentTaskAsync(
            administration, claim, previousMissing, statusMoved, ct);

        return (statusMoved, notified);
    }

    /// <summary>
    /// The fourth criterion. Fires when the dossier has just entered <c>DocumentsRequired</c> or
    /// when the list of pièces changed under it, and not otherwise.
    /// </summary>
    private async Task<bool> RaiseDocumentTaskAsync(
        IAdministrationModule administration,
        ClaimRecord claim,
        string? previousMissing,
        bool statusMoved,
        CancellationToken ct)
    {
        if (claim.Status != ClaimStatus.DocumentsRequired) return false;

        var listChanged = !string.Equals(
            previousMissing, claim.MissingDocuments, StringComparison.Ordinal);

        if (!statusMoved && !listChanged) return false;

        // Nobody to tell. A dossier discovered at the insurer's counter has DeclaredBy = SYSTEM,
        // and a notification addressed to the empty user is a failed send that looks like a bug.
        if (claim.DeclaredBy == Guid.Empty)
        {
            logger.LogInformation(
                "Claim {ClaimId} needs documents and was not declared by a user of this "
                + "institution; no agent task raised", claim.Id);
            return false;
        }

        // Keyed on WHAT the notification is about, as NotifyUserRequest asks: the dossier plus the
        // list of pièces. A repeated sweep that changes nothing therefore re-sends nothing, and a
        // genuinely new request does.
        var idempotencyKey =
            $"{DocumentsRequiredTemplate}-{claim.Id}-{Hash(claim.MissingDocuments)}";

        var sent = await administration.NotifyUserAsync(
            new NotifyUserRequest(
                TenantId: claim.TenantId,
                UserId: claim.DeclaredBy,
                TemplateKey: DocumentsRequiredTemplate,
                IdempotencyKey: idempotencyKey,
                TemplateData: new Dictionary<string, object>
                {
                    // The insurer's dossier number, the date, and the list of pièces. Deliberately
                    // no customer name and no account of the loss: a rendered body travels to a
                    // mail provider and sits in its delivery log.
                    ["claim_number"] = claim.ClaimNumber ?? claim.ExternalClaimId ?? "—",
                    ["occurred_on"] = claim.OccurredOn.ToString("yyyy-MM-dd"),
                    ["missing_documents"] = claim.MissingDocuments ?? "—",
                }),
            ct);

        if (!sent)
        {
            logger.LogWarning(
                "Claim {ClaimId} needs documents but the agent task could not be sent to user "
                + "{UserId}", claim.Id, claim.DeclaredBy);
        }

        return sent;
    }

    /// <summary>
    /// Short, stable digest of the list of pièces, for the idempotency key. Not a security
    /// primitive — it exists so the key does not carry a thousand characters of insurer text.
    /// </summary>
    private static string Hash(string? value)
        => string.IsNullOrEmpty(value)
            ? "none"
            : Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    /// <summary>
    /// The claim port of one connection's adapter, or <c>null</c> when it declares none. A
    /// bordereau-fed insurer takes its declarations on paper; that is a configuration, not a fault,
    /// which is why the absence is debug and not a warning.
    /// </summary>
    private IInsuranceClaimPort? ResolveClaimPort(
        IntegrationAdapterResolver resolver, IntegrationConnection connection)
    {
        var resolved = resolver.ResolveFor(connection);

        if (resolved.IsFailure)
        {
            logger.LogWarning(
                "Claim sync skipped connection {ConnectionId} ({Name}): {Error}",
                connection.Id, connection.Name, resolved.Code);
            return null;
        }

        var port = resolver.ResolvePort<IInsuranceClaimPort>(
            resolved.Value, IntegrationCapability.ReadClaims);

        if (port.IsFailure)
        {
            logger.LogDebug(
                "Connection {ConnectionId} does not support ReadClaims; {Code}",
                connection.Id, port.Code);
            return null;
        }

        return port.Value;
    }
}
