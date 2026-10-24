namespace Sankore.Modules.Leads.Features.Household.SetHouseholdResources;

using MediatR;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel;

internal sealed class SetHouseholdResourcesHandler(
    HouseholdWriteContext context,
    IInstitutionCurrency? institutionCurrency = null)
    : IRequestHandler<SetHouseholdResourcesCommand, Result>
{
    public async Task<Result> Handle(SetHouseholdResourcesCommand cmd, CancellationToken ct)
    {
        // LoadLeadAsync: staleness and ownership, but not the product line's household policy —
        // what the household earns is not a member, and no limit of the line applies to it.
        var loaded = await context.LoadLeadAsync(cmd.LeadId, cmd.ExpectedUpdatedAt, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var lead = loaded.Value.Lead;

        // In the institution's currency — or the one these resources were declared in, before a change.
        var currency = await institutionCurrency.ResolveAsync(
            lead.TenantId, cmd.Currency, lead.HouseholdResourcesCurrency, ct);
        if (currency.IsFailure)
            return currency;

        var declared = lead.DeclareHouseholdResources(
            cmd.MonthlyIncome, cmd.HousingCost, cmd.OtherDebts, currency.Value, context.Clock.GetUtcNow());

        if (declared.IsFailure)
            return declared;

        await context.CommitAsync(lead, ct);
        return Result.Ok();
    }
}
