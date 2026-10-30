namespace Sankore.Modules.Leads.Features.ConvertLead;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
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

/// <summary>
/// OPENS THE CLIENT FILE of a lead — the endpoint keeps its historical name, « convert ». Product
/// owner, 2026-10-24: a lead is CONVERTED when one of its projects is approved
/// (<c>ProjectLeadConversion</c>), not here. What happens here is everything that has to exist
/// before that approval: the client record (created by M01, active at once for a product that
/// requires no KYC), the KYC file a KYC product asks for, the lead attached to the client, and the
/// lead's projects moved onto the client record. The gates — completeness, duplicates, an identity
/// document already on someone else — stay here, because they are about creating a client.
/// </summary>
internal sealed class ConvertLeadHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    ICustomersModule customersModule,
    IAdministrationModule administration,
    ILeadEmailIndexer emailIndexer,
    Features.Enrichment.LeadCompletenessCalculator completeness,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    Sankore.Modules.Workflow.PublicApi.IWorkflowModule? workflow = null)
    : IRequestHandler<ConvertLeadCommand, Result<ConvertLeadResult>>
{
    /// <summary>
    /// The reason recorded on the lead's attachment when this handler CREATED the client — what
    /// tells the later conversion that the lead produced a new customer.
    /// </summary>
    internal const string ClientFileOpenedReason = "Dossier client ouvert depuis le lead";

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
                AlreadyConverted: true,
                LeadConverted:   true));
        }

        // A « bloquante » step of a circuit running on the lead holds the conversion until it is
        // passed (product owner, 2026-10-29). After the already-converted answer: a retry of a
        // conversion that went through must still get its client back.
        if (workflow is not null
            && await workflow.GetBlockingStepsAsync(lead.TenantId, "Lead", lead.Id, ct) is { Count: > 0 } blocking)
            return Result.Fail<ConvertLeadResult>(
                $"Étape bloquante non franchie : {string.Join(", ", blocking)}. Le lead ne peut pas encore être converti.");

        // An open file names its client: naming ANOTHER one is not a choice to honour silently —
        // one of the two records is wrong. Refused before any check that reads the client file.
        if (lead.CustomerId is { } linked && cmd.CustomerId is { } named && named != linked)
            return Result.Fail<ConvertLeadResult>("CUSTOMER_LINK_CONFLICT");

        // ── The client file is already open: nothing to create ───────────
        //
        // A lead attached to a client — opened here earlier, or matched at capture — has its file.
        // Its projects are moved onto it (one opened since), and that is all: what the client file
        // must hold before a project is approved is the approval gate's question, not this one's.
        if (lead.CustomerId is { } attachedClient
            && (cmd.CustomerId is null || cmd.CustomerId == attachedClient))
        {
            await AttachProjectsAsync(lead, attachedClient, ct);
            await db.SaveChangesAsync(ct);

            return Result.Ok(new ConvertLeadResult(
                LeadId:         lead.Id,
                CustomerId:     attachedClient,
                ConvertedAt:    default,
                ConversionType: LeadConversionType.ExistingCustomerNewProduct,
                AlreadyConverted: true));
        }

        // ── Which product is the file opened for? ────────────────────────
        //
        // The PROJECT's (product owner, 2026-10-24): the lead stores no product line, and the line
        // decides the fields required before the client is created, whether a KYC file opens and
        // at which tier. The named project, else the lead's single open one, else — no project
        // yet — what the lead asked for.
        var productLineId = await ProductLines.ProductLineLookup.ForClientFileAsync(
            db, administration, lead, cmd.ProjectId, ct);

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
        var report = await completeness.ComputeAsync(lead, productLineId, ct);
        if (!report.CanConvert)
        {
            return Result.Ok(new ConvertLeadResult(
                LeadId:       cmd.LeadId,
                CustomerId:   Guid.Empty,
                ConvertedAt:  default,
                BlockingCode: Features.Enrichment.LeadCompletenessCalculator.Incomplete,
                Completeness: report));
        }

        // ── Does this product require KYC at all? ───────────────────────
        //
        // A Standard line whose policy says None sells without a file: the client is created
        // active and nothing asks M02 for anything. Anything else — an unresolved product, an
        // unreadable policy — keeps the file, because a gate that cannot tell must not waive it.
        var kycRequired = await IsKycRequiredAsync(lead.TenantId, productLineId, ct);

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

        // ── Open the client file (BNF-B-12) ─────────────────────────────
        //
        // Two ways to end up with a client id: the caller named an existing one, or M01 creates one
        // now. Creation happens BEFORE the lead is attached so a refusal leaves the lead untouched.
        Guid customerId;
        string conversionType;

        if (cmd.CustomerId.HasValue)
        {
            customerId     = cmd.CustomerId.Value;
            conversionType = LeadConversionType.ExistingCustomerNewProduct;
        }
        else
        {
            conversionType = LeadConversionType.NewCustomer;

            var creation = await CreateClientAsync(lead, kycRequired, ct);
            if (creation.IsFailure)
                return Result.Fail<ConvertLeadResult>(creation.Error!);

            var created = creation.Value;

            // M01 found the identity document on someone else. The lead stays as it is and the
            // agent is handed the existing client to attach it to by hand (US-M01-BE-06).
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

        // Attached, NOT converted: the lead converts when one of its projects is approved.
        var attachment = lead.AttachToCustomer(
            customerId,
            isAutomatic: false,
            reason: conversionType == LeadConversionType.NewCustomer
                ? ClientFileOpenedReason
                : "Rattaché à un client existant à l'ouverture du dossier client");
        if (attachment.IsFailure)
            return Result.Fail<ConvertLeadResult>(attachment.Error!);

        await AttachProjectsAsync(lead, customerId, ct);

        // ── US-M13-172: Publish KycRequested event via outbox ───────────
        if (kycRequired)
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
                    HasPepExposure:   lead.HasPepExposure,
                    // KRM-B-02: which catalogue line this file is opened on. M02 decides from it
                    // whether the new customer may send their documents remotely and how the file is
                    // validated. Null when the lead's product label resolved to nothing, which is
                    // ordinary — the file is then simply collected at the counter.
                    ProductLineId:    productLineId),
                ct);

        await db.SaveChangesAsync(ct);

        // No draft holding here any more: the contract is opened when the PROJECT is won
        // (CloseOpportunityHandler), on the project's own product — the file opening is not a sale.
        return Result.Ok(new ConvertLeadResult(
            LeadId:         lead.Id,
            CustomerId:     customerId,
            ConvertedAt:    default,
            ConversionType: conversionType,
            Completeness:   report));
    }

    /// <summary>The lead's projects not yet on a client record move onto this one.</summary>
    private async Task AttachProjectsAsync(Lead lead, Guid customerId, CancellationToken ct)
    {
        var projects = await db.Opportunities
            .AsTracking()
            .Where(o => o.LeadId == lead.Id && o.CustomerEntityId == null)
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var project in projects)
            project.AttachToCustomer(customerId, now);
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
    /// <summary>
    /// False only for a resolved line whose policy says <c>None</c>. Best-effort like the line
    /// resolution above, and fail-closed: an outage reads as « KYC required ».
    /// </summary>
    private async Task<bool> IsKycRequiredAsync(Guid tenantId, Guid? productLineId, CancellationToken ct)
    {
        if (productLineId is not { } lineId)
            return true;

        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress,
                TransactionScopeAsyncFlowOption.Enabled);

            var policy = await administration.GetBeneficiaryPolicyAsync(tenantId, lineId, ct);

            suppressed.Complete();
            return policy is null || KycRequirement.IsRequired(policy.RequiredKycTier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return true;
        }
    }

    private async Task<Result<CreateFromLeadResult>> CreateClientAsync(Lead lead, bool kycRequired, CancellationToken ct)
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
            RequestedClientId:      null,
            CustomFieldsJson:       lead.CustomFieldsJson,
            KycRequired:            kycRequired);

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
