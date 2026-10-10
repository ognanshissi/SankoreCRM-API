namespace Sankore.Modules.Leads.Features.ConvertLead;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ConvertLead.Events;
using Sankore.Modules.Leads.Features.Enrichment;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class ConvertLeadHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    ICustomersModule customersModule,
    ILeadEmailIndexer emailIndexer,
    Features.Enrichment.LeadCompletenessCalculator completeness,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<ConvertLeadCommand, Result<ConvertLeadResult>>
{
    public async Task<Result<ConvertLeadResult>> Handle(
        ConvertLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            // The household is needed for the questionnaire gate below: the expectation is computed
            // over the ACTIVE members, so a lead loaded without them would appear to owe nothing.
            .Include(l => l.HouseholdMembers)
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<ConvertLeadResult>("LEAD_NOT_FOUND");

        // ── Already converted: answer, change nothing (BNF-B-24) ─────────
        //
        // Reached by a retried request and by an agent double-clicking. Returning the existing
        // client is the honest answer to "convert this lead"; falling through would re-publish
        // LeadConverted and tell a back office to open the same holding twice.
        if (lead.Status == LeadStatus.Converted && lead.ConvertedToCustomerId is { } already)
        {
            if (cmd.CustomerId is { } asked && asked != already)
                return Result.Fail<ConvertLeadResult>("LEAD_ALREADY_CONVERTED_TO_ANOTHER_CUSTOMER");

            return Result.Ok(new ConvertLeadResult(
                LeadId:          cmd.LeadId,
                CustomerId:      already,
                ConvertedAt:     lead.ConvertedAt ?? default,
                AlreadyConverted: true));
        }

        // ── The file must be complete (BNF-B-04, BNF-B-08, BNF-B-11) ─────
        //
        // The SAME calculator the completeness endpoint and the enrichment response use, so the
        // screen showing 100% and the conversion accepting the lead cannot disagree. Before Lot 6
        // this gate checked questionnaires only, and a required field or a missing FATCA
        // declaration passed through it.
        //
        // Before the duplicate gate and before M01 is asked to create anything: a conversion that
        // created a client and then refused would leave the client behind, and the idempotency on
        // SourceLeadId would hand the same one back on the retry — correct, but it means the
        // refusal has already had a side effect. No-side-effect checks first.
        //
        // NOT bypassed by Force. That flag exists to override the DUPLICATE gate, which is a
        // judgement about whether two records are the same person; a missing health declaration or
        // an unresolved product line is not a judgement call.
        var report = await completeness.ComputeAsync(lead, ct);
        if (!report.CanConvert)
        {
            return Result.Ok(new ConvertLeadResult(
                LeadId:       cmd.LeadId,
                CustomerId:   Guid.Empty,
                ConvertedAt:  default,
                // The questionnaire code when questionnaires are what is missing, so a client
                // written against Lot 4 keeps working; the general code otherwise.
                BlockingCode: report.MissingQuestionnaires.Count > 0
                    ? Features.Questionnaires.QuestionnaireCompletenessChecker.MissingResponses
                    : Features.Enrichment.LeadCompletenessCalculator.Incomplete,
                MissingQuestionnaires: report.MissingQuestionnaires,
                Completeness:          report));
        }

        // ── US-M13-171: Validate existing customer if provided ──────────
        if (cmd.CustomerId.HasValue)
        {
            // An attached lead already names the client its identity comes from (BNF-B-23). A
            // caller naming a DIFFERENT one is not a choice to honour silently: one of the two
            // records is wrong, and picking either would convert the request against a file the
            // agent never reviewed. Detaching is an explicit act with its own endpoint.
            if (lead.CustomerId is { } attached && attached != cmd.CustomerId.Value)
                return Result.Fail<ConvertLeadResult>("CUSTOMER_LINK_CONFLICT");

            var exists = await customersModule.ExistsAsync(
                lead.TenantId, cmd.CustomerId.Value, ct);

            if (!exists)
                return Result.Fail<ConvertLeadResult>("CUSTOMER_NOT_FOUND");
        }

        // ── Duplicate gate before conversion ─────────────────────────────
        //
        // SKIPPED for a lead that belongs to a known customer (BNF-B-22/B-24). This gate looks for
        // other LEADS matching the same person, and for an existing customer asking for a second
        // product those are the expected state of affairs — the client record lists them as
        // "requests in progress". Blocking on them would make a second subscription impossible.
        // The duplicate question that does apply to such a lead is "does this client already hold
        // this product line", which is AllowMultipleHoldings against the holdings, not lead dedup.
        if (!cmd.Force && lead.Kind == LeadKind.NewProspect)
        {
            var probe = MatchProbe.From(
                lead.PhoneNumber, lead.Email, lead.NationalId, lead.CustomerReference,
                fullName:    lead.FullName,
                dateOfBirth: lead.DateOfBirth,
                latitude:    lead.Location?.Latitude,
                longitude:   lead.Location?.Longitude);

            var phoneDigits = probe.PhoneDigits;
            var emailNorm   = probe.EmailNorm;
            var emailIndex  = emailIndexer.Compute(lead.Email ?? lead.LegacyEmailClear);
            var nationalId  = probe.NationalId;
            var customerRef = probe.CustomerReference;

            if (phoneDigits is not null || emailNorm is not null
                || nationalId is not null || customerRef is not null)
            {
                var candidates = await db.Leads
                    .Where(l =>
                        l.Id != cmd.LeadId &&
                        l.Status != LeadStatus.Lost &&
                        l.Status != LeadStatus.Archived &&
                        l.Status != LeadStatus.Disqualified &&
                        l.Status != LeadStatus.Converted &&
                        ((phoneDigits != null && l.PhoneNumber.EndsWith(phoneDigits)) ||
                         // Blind index, never the encrypted column — see CaptureLeadHandler.
                         (emailIndex != null && l.EmailBlindIndex == emailIndex) ||
                         (emailIndex != null && l.EmailBlindIndex == null
                            && emailNorm != null && l.LegacyEmailClear != null
                            && l.LegacyEmailClear.ToLower() == emailNorm) ||
                         (nationalId != null && l.NationalId != null && l.NationalId.ToLower() == nationalId.ToLower()) ||
                         (customerRef != null && l.CustomerReference != null && l.CustomerReference.ToLower() == customerRef.ToLower())))
                    .ToListAsync(ct);

                if (candidates.Count > 0)
                {
                    var candidateIds  = candidates.Select(l => l.Id).ToList();
                    var dismissedIds  = await db.DuplicateDismissals
                        .Where(d =>
                            (d.LeadId == cmd.LeadId && candidateIds.Contains(d.CandidateLeadId)) ||
                            (d.CandidateLeadId == cmd.LeadId && candidateIds.Contains(d.LeadId)))
                        .Select(d => d.CandidateLeadId == cmd.LeadId ? d.LeadId : d.CandidateLeadId)
                        .ToListAsync(ct);

                    if (dismissedIds.Count > 0)
                        candidates = candidates.Where(l => !dismissedIds.Contains(l.Id)).ToList();
                }

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
                        return Result.Ok(new ConvertLeadResult(
                            LeadId:              cmd.LeadId,
                            CustomerId:          Guid.Empty,
                            ConvertedAt:         default,
                            DuplicateDetected:   true,
                            PotentialDuplicates: matches));
                    }
                }
            }
        }

        // ── Proceed with conversion (BNF-B-12) ──────────────────────────
        //
        // Three ways to end up with a client id, in priority order: the caller named one, the lead
        // is already attached to one (BNF-B-22 — "sans ressaisie": nothing of their identity is
        // re-entered and M01 is not asked to create anything), or M01 creates one now. Creation
        // happens BEFORE lead.Convert so a refusal leaves the lead untouched rather than marking
        // it converted against a client that was never created.
        Guid customerId;
        string conversionType;

        if (cmd.CustomerId.HasValue)
        {
            customerId = cmd.CustomerId.Value;
            // An explicit id on an attached lead is the same client — the conflict was refused
            // above — so this is the existing-customer path either way.
            conversionType = lead.CustomerId.HasValue
                ? LeadConversionType.ExistingCustomerNewProduct
                : LeadConversionType.NewCustomer;
        }
        else if (lead.CustomerId is { } linkedCustomerId)
        {
            customerId     = linkedCustomerId;
            conversionType = LeadConversionType.ExistingCustomerNewProduct;
        }
        else
        {
            conversionType = LeadConversionType.NewCustomer;

            var creation = await CreateClientAsync(lead, ct);
            if (creation.IsFailure)
                return Result.Fail<ConvertLeadResult>(creation.Error!);

            var created = creation.Value;

            // M01 found the identity document on someone else. The lead stays unconverted and
            // the agent is handed the existing client to attach it to by hand (US-M01-BE-06).
            if (created.BlockingCode is { } blockingCode)
            {
                return Result.Ok(new ConvertLeadResult(
                    LeadId:                 cmd.LeadId,
                    CustomerId:             Guid.Empty,
                    ConvertedAt:            default,
                    BlockingCode:           blockingCode,
                    ExistingCustomerId:     created.ClientId,
                    ExistingCustomerNumber: created.ClientNumber));
            }

            customerId = created.ClientId;
        }

        var convertResult = lead.Convert(customerId);
        if (convertResult.IsFailure)
            return Result.Fail<ConvertLeadResult>(convertResult.Error!);

        // Publish LeadConverted integration event atomically via outbox
        await publisher.PublishAsync(
            new LeadConvertedIntegrationEvent(
                LeadId:      lead.Id,
                CustomerId:  customerId,
                TenantId:    lead.TenantId,
                FullName:    lead.FullName,
                PhoneNumber: lead.PhoneNumber,
                Email:       lead.Email,
                ConvertedAt: lead.ConvertedAt!.Value,
                // BNF-B-24: which path ran, and on which catalogue line. A consumer cannot derive
                // either — both paths end with a lead pointing at a client.
                ConversionType: conversionType,
                ProductLineId:  lead.ProductLineId),
            ct);

        // ── US-M13-172: Publish KycRequested event via outbox ───────────
        await publisher.PublishAsync(
            new KycRequestedIntegrationEvent(
                TenantId:         lead.TenantId,
                CustomerEntityId: customerId,
                LeadId:           lead.Id,
                FullName:         lead.FullName,
                PhoneNumber:      lead.PhoneNumber,
                Email:            lead.Email,
                NationalId:       lead.NationalId,
                DateOfBirth:      lead.DateOfBirth,
                RequestedBy:      currentUser.Id,
                // BNF-B-24: an exposed household opens the KYC file at High vigilance, which adds
                // the compliance officer to the approval ladder. The FLAG only — the function, the
                // country and the family link stay behind lead:pep:read.
                HasPepExposure:   lead.HasPepExposure),
            ct);

        await db.SaveChangesAsync(ct);

        // ── The draft holding (BNF-B-24) ─────────────────────────────────
        //
        // AFTER the lead is committed, and BEST-EFFORT. The subscription is a consequence of the
        // conversion, not a condition of it: a lead that produced a client has converted, and
        // failing the whole command because M01 could not open a draft would leave the agent with
        // a client, a converted lead and an error telling them none of it worked.
        //
        // Only when the product line is resolved — there is no line to subscribe to otherwise, and
        // the completeness gate above already reported that.
        var holdingId = await TryOpenHoldingAsync(lead, customerId, ct);

        return Result.Ok(new ConvertLeadResult(
            LeadId:         lead.Id,
            CustomerId:     customerId,
            ConvertedAt:    lead.ConvertedAt!.Value,
            ConversionType: conversionType,
            HoldingId:      holdingId,
            Completeness:   report));
    }

    /// <summary>
    /// Opens the draft product holding a converted lead leaves behind (BNF-B-24), or returns null
    /// when there is nothing to open or M01 refused.
    /// </summary>
    /// <remarks>
    /// <b>Swallows every failure, and logs none of them as an error.</b> Two of the refusals are
    /// ordinary: <c>HOLDING_ALREADY_HELD</c> for a line that forbids a second holding (the client
    /// already has one — the conversion is still correct), and a retried conversion, which M01
    /// answers idempotently. The rest are an M12 or M01 problem the agent can resolve by opening
    /// the subscription by hand on the client record.
    ///
    /// <para>
    /// Suppressed transaction scope, like every other cross-module call on this path: M01 writes
    /// through its own DbContext, and a second connection enlisting in the ambient
    /// <c>System.Transactions</c> scope promotes it to a distributed transaction Npgsql cannot do.
    /// </para>
    /// </remarks>
    private async Task<Guid?> TryOpenHoldingAsync(Lead lead, Guid customerId, CancellationToken ct)
    {
        if (lead.ProductLineId is not { } lineId)
            return null;

        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress,
                TransactionScopeAsyncFlowOption.Enabled);

            var result = await customersModule.CreateHoldingFromLeadAsync(
                new CreateHoldingFromLeadRequest(
                    TenantId:          lead.TenantId,
                    ClientId:          customerId,
                    ProductLineId:     lineId,
                    SourceLeadId:      lead.Id,
                    ConvertedByUserId: currentUser.Id),
                ct);

            suppressed.Complete();

            return result.IsSuccess ? result.Value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>
    /// Creates the client through M01's contract, pre-filling what the lead already collected.
    ///
    /// Runs in a SUPPRESSED transaction scope on purpose. TransactionBehavior has an ambient
    /// scope open around this command, and M01 writes through its own DbContext: two connections
    /// enlisting in one System.Transactions scope promote to a distributed transaction, which
    /// PostgreSQL/Npgsql does not support — it would throw at run time, not at build time.
    ///
    /// What makes that safe is the contract's own guarantee: CreateFromLeadAsync is idempotent on
    /// SourceLeadId. If the lead conversion fails after the client was created, retrying the
    /// conversion returns the SAME client instead of creating a second one.
    /// </summary>
    private async Task<Result<CreateFromLeadResult>> CreateClientAsync(Lead lead, CancellationToken ct)
    {
        // A client belongs to an agency. A lead that was never routed to one cannot become a
        // client without someone choosing where it lands.
        var agencyId = lead.AgencyId ?? lead.PreferredAgencyId;
        if (agencyId is null)
            return Result.Fail<CreateFromLeadResult>("LEAD_HAS_NO_AGENCY");

        var isCompany = string.IsNullOrWhiteSpace(lead.FirstName)
                        && string.IsNullOrWhiteSpace(lead.LastName)
                        && !string.IsNullOrWhiteSpace(lead.CompanyName);

        var (firstName, lastName) = isCompany ? (null, null) : SplitName(lead);

        var request = new CreateFromLeadRequest(
            TenantId:               lead.TenantId,
            LeadId:                 lead.Id,
            AgencyId:               agencyId.Value,
            ConvertedByUserId:      currentUser.Id,
            FirstName:              firstName,
            LastName:               lastName,
            LegalName:              isCompany ? lead.CompanyName : null,
            Gender:                 lead.Gender == LeadGender.Unknown ? null : lead.Gender.ToString(),
            DateOfBirth:            lead.DateOfBirth,
            Nationality:            null,
            PhoneNumber:            lead.PhoneNumber,
            Email:                  lead.Email,
            // A lead only ever carries a national id, never the type of document it came from;
            // M01 infers the type from the number being present.
            IdentityDocumentType:   null,
            IdentityDocumentNumber: lead.NationalId,
            Profession:             null,
            PreferredLanguage:      lead.PreferredLanguage,
            RequestedClientId:      null);

        using var suppressed = new TransactionScope(
            TransactionScopeOption.Suppress,
            TransactionScopeAsyncFlowOption.Enabled);

        var result = await customersModule.CreateFromLeadAsync(request, ct);

        suppressed.Complete();
        return result;
    }

    /// <summary>
    /// Leads captured through a web form often carry only a full name. Splitting on the first
    /// space is a guess, but a wrong guess an agent can correct beats refusing the conversion.
    /// </summary>
    private static (string? First, string? Last) SplitName(Lead lead)
    {
        if (!string.IsNullOrWhiteSpace(lead.FirstName) || !string.IsNullOrWhiteSpace(lead.LastName))
            return (lead.FirstName, lead.LastName ?? lead.FirstName);

        var parts = lead.FullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => (null, null),
            1 => (parts[0], parts[0]),
            _ => (parts[0], parts[1]),
        };
    }
}
