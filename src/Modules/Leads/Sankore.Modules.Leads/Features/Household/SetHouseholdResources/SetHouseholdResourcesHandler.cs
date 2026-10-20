namespace Sankore.Modules.Leads.Features.Household.SetHouseholdResources;

using MediatR;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel;

internal sealed class SetHouseholdResourcesHandler(HouseholdWriteContext context)
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

        var declared = lead.DeclareHouseholdResources(
            cmd.MonthlyIncome, cmd.HousingCost, cmd.OtherDebts, cmd.Currency, context.Clock.GetUtcNow());

        if (declared.IsFailure)
            return declared;

        await context.CommitAsync(lead, ct);
        return Result.Ok();
    }
}
