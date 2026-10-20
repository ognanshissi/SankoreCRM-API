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

        var previousProduct = lead.InterestedProduct;

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

        // The line follows the label. Left alone, a lead whose product an agent corrected kept the
        // line resolved from the OLD label — or none at all — and the conversion then sent the
        // KYC file and the draft holding the wrong product, or no product.
        if (!string.Equals(previousProduct?.Trim(), lead.InterestedProduct?.Trim(), StringComparison.OrdinalIgnoreCase))
            await ResolveProductLineAsync(lead, ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    /// <summary>
    /// Same rule as at capture. A label that matches nothing CLEARS the line: keeping one that
    /// names another product would be worse than none. A catalogue outage changes nothing — the
    /// edit is saved and the conversion retries the lookup.
    /// </summary>
    private async Task ResolveProductLineAsync(Lead lead, CancellationToken ct)
    {
        Guid? resolved;
        try
        {
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress,
                TransactionScopeAsyncFlowOption.Enabled);

            resolved = await administration.ResolveProductLineAsync(
                lead.TenantId, lead.InterestedProduct, ct);

            suppressed.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return;
        }

        if (resolved != lead.ProductLineId)
            lead.SetProductLine(resolved);
    }
}
