namespace Sankore.Modules.Leads.Features.ConvertLead;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customer360.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ConvertLead.Events;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class ConvertLeadHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    ICustomersModule customersModule,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<ConvertLeadCommand, Result<ConvertLeadResult>>
{
    public async Task<Result<ConvertLeadResult>> Handle(
        ConvertLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<ConvertLeadResult>("LEAD_NOT_FOUND");

        // ── US-M13-171: Validate existing customer if provided ──────────
        if (cmd.CustomerId.HasValue)
        {
            var exists = await customersModule.ExistsAsync(
                lead.TenantId, cmd.CustomerId.Value, ct);

            if (!exists)
                return Result.Fail<ConvertLeadResult>("CUSTOMER_NOT_FOUND");
        }

        // ── Duplicate gate before conversion ─────────────────────────────
        if (!cmd.Force)
        {
            var probe = MatchProbe.From(
                lead.PhoneNumber, lead.Email, lead.NationalId, lead.CustomerReference,
                fullName:    lead.FullName,
                dateOfBirth: lead.DateOfBirth,
                latitude:    lead.Location?.Latitude,
                longitude:   lead.Location?.Longitude);

            var phoneDigits = probe.PhoneDigits;
            var emailNorm   = probe.EmailNorm;
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
                         (emailNorm != null && l.Email != null && l.Email.ToLower() == emailNorm) ||
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

        // ── Proceed with conversion ─────────────────────────────────────
        // Either the caller attached an existing customer (already validated above), or M01
        // creates one now. Creation happens BEFORE lead.Convert so a refusal leaves the lead
        // untouched rather than marking it converted against a client that was never created.
        Guid customerId;

        if (cmd.CustomerId.HasValue)
        {
            customerId = cmd.CustomerId.Value;
        }
        else
        {
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
                ConvertedAt: lead.ConvertedAt!.Value),
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
                RequestedBy:      currentUser.Id),
            ct);

        await db.SaveChangesAsync(ct);

        return Result.Ok(new ConvertLeadResult(
            LeadId:      lead.Id,
            CustomerId:  customerId,
            ConvertedAt: lead.ConvertedAt!.Value));
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
