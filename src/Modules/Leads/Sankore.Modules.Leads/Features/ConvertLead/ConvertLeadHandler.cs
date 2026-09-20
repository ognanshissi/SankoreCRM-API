namespace Sankore.Modules.Leads.Features.ConvertLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customer360.PublicApi;
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
    ICustomerModule customerModule,
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
            var exists = await customerModule.ExistsAsync(
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
        var customerId = cmd.CustomerId ?? Guid.NewGuid();

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
}
