namespace Sankore.Modules.Leads.Features.UpdateLead;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CustomFields;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Money = Sankore.Shared.Kernel.ValueObject.Money;

internal sealed class UpdateLeadHandler(
    LeadsDbContext db,
    ILeadEmailIndexer emailIndexer,
    IAdministrationModule administration,
    ILogger<UpdateLeadHandler> logger)
    : IRequestHandler<UpdateLeadCommand, Result>
{
    public async Task<Result> Handle(UpdateLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        if (lead.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail(LeadConcurrency.ConflictError);


        GeoPoint? location = null;
        if (cmd.Latitude.HasValue && cmd.Longitude.HasValue)
            location = new GeoPoint(cmd.Latitude.Value, cmd.Longitude.Value);

        var result = lead.Update(
            fullName:         cmd.FullName,
            firstName:        cmd.FirstName,
            lastName:         cmd.LastName,
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

        // The e-mail goes through its own setter so the blind index is rewritten with it — see
        // Lead.SetEmail. A null cmd.Email means "not supplied" here, as for every other field of
        // this command, so it leaves the stored address alone rather than clearing it.
        if (cmd.Email is not null)
        {
            var emailResult = lead.SetEmail(cmd.Email, emailIndexer.Compute(cmd.Email));
            if (emailResult.IsFailure)
                return emailResult;
        }

        // Custom fields: merged into what the lead holds, and a required one may not be cleared.
        if (cmd.CustomFields is { Count: > 0 })
        {
            var custom = await LeadFormGate.ApplyAsync(
                administration, logger, lead.TenantId, lead.CustomFieldsJson, cmd.CustomFields,
                new Dictionary<string, string?> { ["prospectType"] = lead.ProspectType.ToString() },
                enforceRequired: true, isCreate: false, ct);

            if (custom.IsFailure)
                return Result.Fail(custom.Error!);

            var stored = lead.SetCustomFields(custom.Value);
            if (stored.IsFailure)
                return stored;
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
