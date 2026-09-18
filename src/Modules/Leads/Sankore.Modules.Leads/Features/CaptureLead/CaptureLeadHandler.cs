using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
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
    IBus bus)
    : IRequestHandler<CaptureLeadCommand, Result<CaptureLeadResult>>
{
    public async Task<Result<CaptureLeadResult>> Handle(
        CaptureLeadCommand cmd, CancellationToken ct)
    {
        // ── Duplicate detection (multi-signal identity scoring) ──────────────
        IReadOnlyList<DuplicateMatchResult>? warnMatches = null;

        if (!cmd.Force)
        {
            var probe = MatchProbe.From(
                cmd.PhoneNumber, cmd.Email, cmd.NationalId, cmd.CustomerReference,
                cmd.FullName, cmd.DateOfBirth,
                latitude:  cmd.Latitude  == 0 && cmd.Longitude == 0 ? null : cmd.Latitude,
                longitude: cmd.Longitude == 0 && cmd.Latitude  == 0 ? null : cmd.Longitude);

            var phoneDigits = probe.PhoneDigits;
            var emailNorm   = probe.EmailNorm;
            var nationalId  = probe.NationalId;
            var customerRef = probe.CustomerReference;

            var candidates = await db.Leads
                .Where(l =>
                    l.Status != LeadStatus.Lost &&
                    l.Status != LeadStatus.Archived &&
                    l.Status != LeadStatus.Disqualified &&
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

        // ── Create the lead ───────────────────────────────────────────────────
        var lead = Lead.Capture(
            tenantId:             cmd.TenantId,
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
            gender:               cmd.Gender,
            dateOfBirth:          cmd.DateOfBirth,
            desiredAmount:        cmd.DesiredAmount.HasValue && cmd.DesiredCurrency is not null
                                      ? new Money(cmd.DesiredAmount.Value, cmd.DesiredCurrency)
                                      : null,
            campaign:             cmd.Campaign,
            channel:              cmd.Channel,
            comment:              cmd.Comment,
            externalReference:    cmd.ExternalReference,
            ownerId:              cmd.OwnerId,
            agencyId:             cmd.AgencyId,
            agentCollectedLeadId: cmd.AgentCollectedLeadId,
            prospectType:         cmd.ProspectType,
            nationalId:           cmd.NationalId,
            customerReference:    cmd.CustomerReference);

        db.Leads.Add(lead);
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
            PotentialDuplicates: warnMatches));
    }
}
