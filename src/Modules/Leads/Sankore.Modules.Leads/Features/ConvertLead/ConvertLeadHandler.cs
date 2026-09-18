namespace Sankore.Modules.Leads.Features.ConvertLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ConvertLead.Events;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class ConvertLeadHandler(
    LeadsDbContext db,
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

        // ── Duplicate gate before conversion ─────────────────────────────────
        // Checks for other active leads with the same identity at a higher
        // confidence threshold than capture (default 70 = Probable).
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

            // Only search if at least one strong identifier is available.
            if (phoneDigits is not null || emailNorm is not null
                || nationalId is not null || customerRef is not null)
            {
                var candidates = await db.Leads
                    .Where(l =>
                        l.Id != cmd.LeadId &&                       // exclude itself
                        l.Status != LeadStatus.Lost &&
                        l.Status != LeadStatus.Archived &&
                        l.Status != LeadStatus.Disqualified &&
                        l.Status != LeadStatus.Converted &&         // already-converted are not a risk
                        ((phoneDigits != null && l.PhoneNumber.EndsWith(phoneDigits)) ||
                         (emailNorm != null && l.Email != null && l.Email.ToLower() == emailNorm) ||
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
                        // Conversion would risk creating a duplicate customer.
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

        // ── Proceed with conversion ───────────────────────────────────────────
        // If the caller didn't supply a CustomerId we generate one here.
        // The Customers module will use this same Guid when it creates the
        // customer record in response to the LeadConvertedIntegrationEvent.
        var customerId = cmd.CustomerId ?? Guid.NewGuid();

        var convertResult = lead.Convert(customerId);
        if (convertResult.IsFailure)
            return Result.Fail<ConvertLeadResult>(convertResult.Error!);

        // Publish the integration event atomically with the lead state change.
        // OutboxEventPublisher writes the row into THIS DbContext instance
        // (no SaveChangesAsync inside), so both rows land in the same commit.
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

        await db.SaveChangesAsync(ct);

        return Result.Ok(new ConvertLeadResult(
            LeadId:      lead.Id,
            CustomerId:  customerId,
            ConvertedAt: lead.ConvertedAt!.Value));
    }
}
