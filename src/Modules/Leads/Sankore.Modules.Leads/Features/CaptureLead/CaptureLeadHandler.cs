using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
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
        // ── Duplicate detection (phone OR email match on active leads) ────────
        if (!cmd.Force)
        {
            var phoneNorm = cmd.PhoneNumber.Trim();
            var emailNorm = cmd.Email?.Trim().ToLowerInvariant();

            var duplicates = await db.Leads
                .Where(l => l.Status != LeadStatus.Lost
                         && l.Status != LeadStatus.Archived
                         && l.Status != LeadStatus.Disqualified
                         && (l.PhoneNumber == phoneNorm
                             || (emailNorm != null && l.Email != null
                                 && l.Email.ToLower() == emailNorm)))
                .ToListAsync(ct);

            if (duplicates.Count > 0)
            {
                logger.LogInformation(
                    "Duplicate gate triggered for phone {Phone} — {Count} match(es) found",
                    phoneNorm, duplicates.Count);

                var matches = duplicates.Select(l =>
                {
                    var matchedOn = new List<string>();
                    if (l.PhoneNumber == phoneNorm) matchedOn.Add("phone");
                    if (emailNorm != null && l.Email != null &&
                        string.Equals(l.Email, emailNorm, StringComparison.OrdinalIgnoreCase))
                        matchedOn.Add("email");

                    return new PotentialDuplicateMatch(
                        l.Id, l.FullName, l.PhoneNumber, l.Email,
                        l.Status.ToString(), matchedOn);
                }).ToList();

                return Result.Ok(new CaptureLeadResult(
                    LeadId:              null,
                    Status:              null,
                    DuplicateDetected:   true,
                    PotentialDuplicates: matches));
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
            prospectType:         cmd.ProspectType);

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

        return Result.Ok(new CaptureLeadResult(lead.Id, lead.Status.ToString()));
    }
}
