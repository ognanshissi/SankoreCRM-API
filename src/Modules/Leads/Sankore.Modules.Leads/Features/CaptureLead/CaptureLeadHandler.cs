using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Leads.Features.CaptureLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// F13.1 (multi-channel capture) + F13.2 (duplicate detection, simplified
/// here to a phone-number match — production version also matches on
/// national ID and biometric face match once available).
/// </summary>
public sealed class CaptureLeadHandler(
    LeadsDbContext db,
    ILogger<CaptureLeadHandler> logger,
    TimeProvider clock)
    : IRequestHandler<CaptureLeadCommand, Result<CaptureLeadResult>>
{
    public async Task<Result<CaptureLeadResult>> Handle(
        CaptureLeadCommand cmd, CancellationToken ct)
    {
        // F13.2: simple duplicate detection by phone number within the tenant.
        var existing = await db.Leads
            .Where(l => l.PhoneNumber == cmd.PhoneNumber && l.Status != LeadStatus.Lost)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            logger.LogInformation(
                "Duplicate lead capture attempt for phone {Phone}, existing lead {LeadId}",
                cmd.PhoneNumber, existing.Id);

            return Result.Ok(new CaptureLeadResult(existing.Id, existing.Status.ToString()));
        }

        var lead = Lead.Capture(
            tenantId: cmd.TenantId,
            fullName: cmd.FullName,
            phoneNumber: cmd.PhoneNumber,
            source: cmd.Source,
            interestedProduct: cmd.InterestedProduct,
            preferredLanguage: cmd.PreferredLanguage,
            location: new GeoPoint(cmd.Latitude, cmd.Longitude),
            preferredAgencyId: cmd.PreferredAgencyId,
            clock: clock);

        db.Leads.Add(lead);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Lead {LeadId} captured from source {Source}", lead.Id, cmd.Source);

        return Result.Ok(new CaptureLeadResult(lead.Id, lead.Status.ToString()));
    }
}
