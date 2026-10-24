using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CustomerLink;
using Sankore.Modules.Leads.Features.CustomFields;
using Sankore.Modules.Leads.Features.CustomerLink.Events;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Money = Sankore.Shared.Kernel.ValueObject.Money;

namespace Sankore.Modules.Leads.Features.CaptureLead;

/// <summary>
/// F13.1 (multi-channel capture) + F13.2 (duplicate detection by phone).
/// </summary>
public sealed class CaptureLeadHandler(
    LeadsDbContext db,
    ILogger<CaptureLeadHandler> logger,
    TimeProvider clock,
    IBus bus,
    IPhoneBlindIndexer phoneBlindIndexer,
    ILeadEmailIndexer emailIndexer,
    ICustomersModule customers,
    IAdministrationModule administration,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    IInstitutionCurrency? institutionCurrency = null)
    : IRequestHandler<CaptureLeadCommand, Result<CaptureLeadResult>>
{
    public async Task<Result<CaptureLeadResult>> Handle(
        CaptureLeadCommand cmd, CancellationToken ct)
    {
        // ── The tenant's lead form (dynamic forms) ───────────────────────────
        //
        // First, before the duplicate gate: a request refused for a malformed custom value should
        // say so, not answer 409 and be refused again once the agent forced it through.
        var customFields = await LeadFormGate.ApplyAsync(
            administration, logger, cmd.TenantId, currentJson: null,
            cmd.CustomFields, NativeFormValues(cmd),
            enforceRequired: cmd.EnforceFormRequirements, isCreate: true, ct);

        if (customFields.IsFailure)
            return Result.Fail<CaptureLeadResult>(customFields.Error!);

        // ── The desired amount, in the institution's currency ────────────────
        Money? desiredAmount = null;
        if (cmd.DesiredAmount is { } desired)
        {
            var currency = await institutionCurrency.ResolveAsync(cmd.TenantId, cmd.DesiredCurrency, stored: null, ct);
            if (currency.IsFailure) return Result.Fail<CaptureLeadResult>(currency.Error!);
            desiredAmount = new Money(desired, currency.Value);
        }

        // ── Blind indexes (HMAC-SHA256) — the searchable half of the protected columns ────
        var phoneIndex = phoneBlindIndexer.Compute(cmd.PhoneNumber);
        var emailIndex = emailIndexer.Compute(cmd.Email);

        // ── Duplicate detection (multi-signal identity scoring) ──────────────
        IReadOnlyList<DuplicateMatchResult>? warnMatches = null;

        if (!cmd.Force)
        {
            var probe = MatchProbe.From(
                cmd.PhoneNumber, cmd.Email, cmd.NationalId, cmd.CustomerReference,
                cmd.FullName, cmd.DateOfBirth,
                latitude:  cmd.Latitude  == 0 && cmd.Longitude == 0 ? null : cmd.Latitude,
                longitude: cmd.Longitude == 0 && cmd.Latitude  == 0 ? null : cmd.Longitude);

            var emailNorm   = probe.EmailNorm;
            var nationalId  = probe.NationalId;
            var customerRef = probe.CustomerReference;

            // Primary phone dedup uses the blind index (indexed, exact match).
            // Falls back to suffix match for legacy leads without a blind index.
            var phoneDigits = probe.PhoneDigits;

            var candidates = await db.Leads
                .Where(l =>
                    l.Status != LeadStatus.Lost &&
                    l.Status != LeadStatus.Archived &&
                    l.Status != LeadStatus.Disqualified &&
                    ((l.PhoneBlindIndex != null && l.PhoneBlindIndex == phoneIndex) ||
                     (l.PhoneBlindIndex == null && phoneDigits != null && l.PhoneNumber.EndsWith(phoneDigits)) ||
                     // E-mail dedup reads the BLIND INDEX, never the e-mail column: that column is
                     // non-deterministic ciphertext, so comparing it matches nothing — silently,
                     // because it compiles and runs. The second branch is the transitional fallback
                     // for leads captured before the encryption migration, whose index
                     // BackfillLeadEmailProtectionJob has not filled yet; it mirrors the phone
                     // fallback above and is removed with the legacy column.
                     (emailIndex != null && l.EmailBlindIndex == emailIndex) ||
                     (emailIndex != null && l.EmailBlindIndex == null
                        && emailNorm != null && l.LegacyEmailClear != null
                        && l.LegacyEmailClear.ToLower() == emailNorm) ||
                     (nationalId != null && l.NationalId != null && l.NationalId.ToLower() == nationalId.ToLower()) ||
                     (customerRef != null && l.CustomerReference != null && l.CustomerReference.ToLower() == customerRef.ToLower())))
                .ToListAsync(ct);

            if (candidates.Count > 0)
            {
                var scorer  = new IdentityMatchScorer();
                var matches = candidates
                    .Select(l => scorer.Score(l, probe, cmd.MinConfidenceThreshold))
                    .Where(r => r is not null)
                    .Select(r => r!)
                    .OrderByDescending(r => r.ConfidenceScore)
                    .ToList();

                if (matches.Count > 0)
                {
                    logger.LogInformation(
                        "Duplicate gate triggered for phone {Phone} — {Count} match(es), mode={Mode}",
                        cmd.PhoneNumber, matches.Count, cmd.GateMode);

                    if (cmd.GateMode == DuplicateGateMode.Block)
                    {
                        return Result.Ok(new CaptureLeadResult(
                            LeadId:              null,
                            Status:              null,
                            DuplicateDetected:   true,
                            PotentialDuplicates: matches));
                    }

                    // Warn mode: fall through to create the lead; carry matches for response.
                    warnMatches = matches;
                }
            }
        }

        // ── Is this a customer we already hold? (BNF-B-22) ───────────────────
        //
        // Attached automatically ONLY on a unique, person-identifying match — see
        // CustomerMatchPolicy. Anything less is left for an agent to confirm through
        // GET leads/{id}/customer-matches, because BNF-B-22 forbids a silent attachment and a
        // wrong one redirects the whole request at another person's client record (BNF-B-23).
        //
        // An M01 OUTAGE MUST NOT COST THE LEAD. A prospect on the phone is the thing being
        // captured; recognising them is an enrichment of it. So the lookup is best-effort and its
        // failure is logged, not propagated — the agent can still attach by hand afterwards.
        Guid? matchedCustomerId = null;

        if (cmd.CustomerId is { } explicitCustomerId && explicitCustomerId != Guid.Empty)
        {
            // A caller that already knows whose request this is — a counter clerk opening a second
            // product for the client in front of them. Trusted, because reaching this command at
            // all required authentication and the agency perimeter; the public ingest contracts do
            // not carry the field.
            matchedCustomerId = explicitCustomerId;
        }
        else
        {
            matchedCustomerId = await TryMatchCustomerAsync(cmd, ct);
        }

        // ── Create the lead ───────────────────────────────────────────────────
        var lead = Lead.Capture(
            tenantId:             cmd.TenantId,
            // Passed through as captured. Lead.Capture decides what FullName holds — structured
            // names win, an explicit full name is the fallback for the contracts that carry only
            // it, and a full name is never split (BNF-B-01). One rule, one place.
            fullName:             cmd.FullName,
            phoneNumber:          cmd.PhoneNumber,
            source:               cmd.Source,
            interestedProduct:    cmd.InterestedProduct,
            preferredLanguage:    cmd.PreferredLanguage,
            location:             new GeoPoint(cmd.Latitude, cmd.Longitude),
            preferredAgencyId:    cmd.PreferredAgencyId,
            clock:                clock,
            firstName:            cmd.FirstName,
            lastName:             cmd.LastName,
            email:                cmd.Email,
            emailBlindIndex:      emailIndex,
            customerId:           matchedCustomerId,
            customerLinkIsAutomatic: cmd.CustomerId is null,
            customerLinkReason:   cmd.CustomerId is null
                                      ? "Rattachement automatique : correspondance unique et exacte à la capture"
                                      : "Client renseigné par l'appelant à la capture",
            gender:               cmd.Gender,
            dateOfBirth:          cmd.DateOfBirth,
            desiredAmount:        desiredAmount,
            campaign:             cmd.Campaign,
            channel:              cmd.Channel,
            comment:              cmd.Comment,
            externalReference:    cmd.ExternalReference,
            ownerId:              cmd.OwnerId,
            agencyId:             cmd.AgencyId,
            agentCollectedLeadId: cmd.AgentCollectedLeadId,
            prospectType:         cmd.ProspectType,
            nationalId:           cmd.NationalId,
            customerReference:    cmd.CustomerReference,
            phoneBlindIndex:      phoneIndex,
            leadSourceConfigId:   cmd.LeadSourceConfigId);

        if (customFields.Value is not null)
            lead.SetCustomFields(customFields.Value);

        db.Leads.Add(lead);

        // ── The household and the questionnaires of v2 (BNF-B-18) ───────────
        //
        // In the SAME transaction as the lead. A household recorded by a second request is one
        // that can fail on its own, leaving a lead whose family somebody has to remember to
        // re-enter — and a health declaration in that position is worse: the consent was given
        // and the answers are gone.
        var extras = await CaptureExtras.ApplyAsync(db, lead, cmd, clock.GetUtcNow(), ct);

        if (extras.IsFailure)
        {
            // Nothing is committed: the lead, its outbox rows and the half-built household all
            // live in this one unit of work, so returning here leaves no trace of a capture that
            // was refused. The caller gets a code it can act on rather than a lead missing the
            // family it just typed.
            return Result.Fail<CaptureLeadResult>(extras.Error!);
        }

        // Outbox row written into THIS DbContext so it commits with the lead: a captured lead
        // whose event was lost would never be dispatched, and an event without its lead would
        // dispatch a row that does not exist.
        await publisher.PublishAsync(
            new Events.LeadCapturedEvent(
                LeadId:             lead.Id,
                TenantId:           cmd.TenantId,
                Source:             lead.Source.ToString(),
                HasExplicitOwner:   cmd.OwnerId.HasValue,
                DuplicateSuspected: warnMatches is not null),
            ct);

        if (lead.CustomerId is { } linkedCustomerId)
        {
            await publisher.PublishAsync(
                new LeadLinkedToCustomerIntegrationEvent(
                    TenantId:    cmd.TenantId,
                    LeadId:      lead.Id,
                    CustomerId:  linkedCustomerId,
                    IsAutomatic: lead.CustomerLinkIsAutomatic),
                ct);
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Lead {LeadId} captured from source {Source} (forced: {Force})",
            lead.Id, cmd.Source, cmd.Force);

        await bus.Publish(new WorkflowTriggerSignal(
            TenantId:   cmd.TenantId,
            EntityType: "Lead",
            EntityId:   lead.Id,
            EventName:  "LEAD_CAPTURED",
            Context: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["status"]            = lead.Status.ToString(),
                ["score"]             = (double)lead.Score,
                ["source"]            = lead.Source.ToString(),
                ["interestedProduct"] = lead.InterestedProduct ?? string.Empty,
                ["preferredLanguage"] = lead.PreferredLanguage ?? string.Empty,
            }), ct);

        return Result.Ok(new CaptureLeadResult(
            LeadId:              lead.Id,
            Status:              lead.Status.ToString(),
            DuplicateDetected:   warnMatches is not null,
            PotentialDuplicates: warnMatches,
            LinkedCustomerId:    lead.CustomerId));
    }

    /// <summary>
    /// Asks M01 whether this prospect is already a client, and returns the id only when the match
    /// is strong enough to act on without asking anybody (BNF-B-22, <see cref="CustomerMatchPolicy"/>).
    /// </summary>
    /// <remarks>
    /// Two deliberate choices here.
    ///
    /// <para>
    /// <b>The ambient transaction is SUPPRESSED.</b> TransactionBehavior has a
    /// <c>System.Transactions</c> scope open around this command and M01 reads through its own
    /// DbContext; a second connection enlisting in that scope promotes it to a distributed
    /// transaction, which Npgsql does not support — and it throws at run time, not at build time.
    /// The same reason <c>ConvertLeadHandler</c> suppresses around <c>CreateFromLeadAsync</c>.
    /// Safe because this is a READ: nothing to roll back if the capture then fails.
    /// </para>
    ///
    /// <para>
    /// <b>A failure is swallowed, on purpose.</b> The thing being captured is a prospect, often
    /// with somebody on the phone; recognising them as an existing client is an enrichment of that,
    /// not a precondition for it. If M01 is down, losing the lead would be the worse outcome by a
    /// wide margin — and the agent can still attach by hand through
    /// <c>PUT leads/{id}/customer</c>. It is logged at warning so an outage is visible rather
    /// than merely survived.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Resolves <c>InterestedProduct</c> onto a product line id, or null (BNF-B-01).
    /// </summary>
    /// <remarks>
    /// Same two choices as <see cref="TryMatchCustomerAsync"/>, for the same reasons: the ambient
    /// transaction is SUPPRESSED because M12 reads through its own DbContext and a second
    /// connection enlisting would promote the scope to a distributed transaction Npgsql cannot do;
    /// and a failure is swallowed, because an unresolved product line is a lead an agent completes
    /// rather than a lead nobody captured.
    ///
    /// <para>
    /// The read is cheap in the common case: M12 caches the tenant's active catalogue for five
    /// minutes, so this is one query per tenant per five minutes rather than one per lead.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The capture's native values under their form keys — what a tenant-required native field and
    /// a <c>prospectType</c> condition are checked against.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> NativeFormValues(CaptureLeadCommand cmd) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["prospectType"]       = cmd.ProspectType.ToString(),
            ["firstName"]          = cmd.FirstName,
            ["lastName"]           = cmd.LastName,
            ["gender"]             = cmd.Gender == LeadGender.Unknown ? null : cmd.Gender.ToString(),
            ["dateOfBirth"]        = cmd.DateOfBirth?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["nationalId"]         = cmd.NationalId,
            ["companyName"]        = cmd.CompanyName,
            ["companyEmail"]       = cmd.CompanyEmail,
            ["companyPhone"]       = cmd.CompanyPhone,
            ["website"]            = cmd.Website,
            ["phoneNumber"]        = cmd.PhoneNumber,
            ["email"]              = cmd.Email,
            ["preferredLanguage"]  = cmd.PreferredLanguage,
            ["interestedProduct"]  = cmd.InterestedProduct,
            ["desiredAmount"]      = cmd.DesiredAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["comment"]            = cmd.Comment,
            ["source"]             = cmd.Source.ToString(),
            ["leadSourceConfigId"] = cmd.LeadSourceConfigId?.ToString(),
            ["channel"]            = cmd.Channel?.ToString(),
            ["campaign"]           = cmd.Campaign,
            ["externalReference"]  = cmd.ExternalReference,
            ["customerReference"]  = cmd.CustomerReference,
            ["agencyId"]           = cmd.AgencyId?.ToString(),
            ["preferredAgencyId"]  = cmd.PreferredAgencyId?.ToString(),
            ["ownerId"]            = cmd.OwnerId?.ToString(),
        };


    private async Task<Guid?> TryMatchCustomerAsync(CaptureLeadCommand cmd, CancellationToken ct)
    {
        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            // Clear values: M01 holds a different blind-index key and indexes them itself.
            var matches = await customers.FindMatchesAsync(
                cmd.TenantId, cmd.PhoneNumber, cmd.Email, cmd.NationalId, ct);

            suppressed.Complete();

            if (matches.Count == 0)
                return null;

            var single = matches[0];
            if (CustomerMatchPolicy.IsAutoAttachable(single, matches.Count))
            {
                logger.LogInformation(
                    "Capture matched existing customer {CustomerId} on {MatchedOn} — attaching.",
                    single.ClientId, string.Join('+', single.MatchedOn));
                return single.ClientId;
            }

            // Deliberately NOT attached. The suggestion endpoint will surface these.
            logger.LogInformation(
                "Capture found {Count} possible customer(s) but none attachable automatically; "
                + "left for an agent to confirm.",
                matches.Count);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Existing-customer lookup failed at capture; the lead is created unattached.");
            return null;
        }
    }
}
