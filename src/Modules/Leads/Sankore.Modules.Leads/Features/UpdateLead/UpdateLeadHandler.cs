namespace Sankore.Modules.Leads.Features.UpdateLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Money = Sankore.Shared.Kernel.ValueObject.Money;

internal sealed class UpdateLeadHandler(LeadsDbContext db)
    : IRequestHandler<UpdateLeadCommand, Result>
{
    public async Task<Result> Handle(UpdateLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        GeoPoint? location = null;
        if (cmd.Latitude.HasValue && cmd.Longitude.HasValue)
            location = new GeoPoint(cmd.Latitude.Value, cmd.Longitude.Value);

        var result = lead.Update(
            fullName:         cmd.FullName,
            firstName:        cmd.FirstName,
            lastName:         cmd.LastName,
            email:            cmd.Email,
            gender:           cmd.Gender,
            dateOfBirth:      cmd.DateOfBirth,
            interestedProduct: cmd.InterestedProduct,
            desiredAmount:    cmd.DesiredAmount.HasValue && cmd.DesiredCurrency is not null
                                  ? new Money(cmd.DesiredAmount.Value, cmd.DesiredCurrency)
                                  : null,
            preferredLanguage: cmd.PreferredLanguage,
            campaign:         cmd.Campaign,
            comment:          cmd.Comment,
            location:         location,
            preferredAgencyId: cmd.PreferredAgencyId);

        if (result.IsFailure)
            return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
