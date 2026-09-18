namespace Sankore.Modules.Leads.Features.Import;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed class ImportLeadsHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<ImportLeadsCommand, Result<LeadImportResult>>
{
    public async Task<Result<LeadImportResult>> Handle(ImportLeadsCommand cmd, CancellationToken ct)
    {
        // Pre-load all phone numbers that already exist for this tenant to batch-check duplicates.
        var incomingPhones = cmd.Rows.Select(r => r.PhoneNumber.Trim()).Distinct().ToList();

        var existingPhones = await db.Leads
            .Where(l => incomingPhones.Contains(l.PhoneNumber)
                     && l.Status != LeadStatus.Lost
                     && l.Status != LeadStatus.Archived
                     && l.Status != LeadStatus.Disqualified)
            .Select(l => l.PhoneNumber)
            .ToHashSetAsync(ct);

        var failures = new List<ImportRowFailure>();
        var newLeads = new List<Lead>();
        int skipped = 0;

        for (int i = 0; i < cmd.Rows.Count; i++)
        {
            var row = cmd.Rows[i];
            var phone = row.PhoneNumber.Trim();

            if (existingPhones.Contains(phone))
            {
                skipped++;
                continue;
            }

            try
            {
                var lead = Lead.Capture(
                    tenantId:          cmd.TenantId,
                    fullName:          row.FullName,
                    phoneNumber:       phone,
                    source:            row.Source,
                    interestedProduct: row.InterestedProduct,
                    preferredLanguage: row.PreferredLanguage,
                    location:          new GeoPoint(row.Latitude, row.Longitude),
                    preferredAgencyId: null,
                    clock:             clock,
                    firstName:         row.FirstName,
                    lastName:          row.LastName,
                    email:             row.Email,
                    gender:            row.Gender,
                    dateOfBirth:       row.DateOfBirth,
                    desiredAmount:     row.DesiredAmount.HasValue && row.DesiredCurrency is not null
                                           ? new Money(row.DesiredAmount.Value, row.DesiredCurrency)
                                           : null,
                    campaign:          row.Campaign,
                    channel:           row.Channel,
                    comment:           row.Comment,
                    externalReference: row.ExternalReference,
                    ownerId:           row.OwnerId,
                    agencyId:          row.AgencyId);

                newLeads.Add(lead);
                // Track phone so subsequent rows with the same phone are also skipped.
                existingPhones.Add(phone);
            }
            catch (Exception ex)
            {
                failures.Add(new ImportRowFailure(i + 1, phone, ex.Message));
            }
        }

        if (newLeads.Count > 0)
        {
            db.Leads.AddRange(newLeads);
            await db.SaveChangesAsync(ct);
        }

        return Result.Ok(new LeadImportResult(
            Succeeded: newLeads.Count,
            Skipped:   skipped,
            Failed:    failures.Count,
            Failures:  failures));
    }
}
