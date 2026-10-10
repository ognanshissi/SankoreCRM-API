namespace Sankore.Modules.Leads.Features.Household.SetPepStatus;

using MediatR;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel;

internal sealed class SetPrincipalPepStatusHandler(HouseholdWriteContext context)
    : IRequestHandler<SetPrincipalPepStatusCommand, Result>
{
    public async Task<Result> Handle(SetPrincipalPepStatusCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.LeadId, cmd.ExpectedUpdatedAt, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var scope = loaded.Value;

        var pep = HouseholdMapping.ToPepStatus(cmd.Pep);
        if (pep.IsFailure)
            return Result.Fail(pep.Error!);

        var set = scope.Lead.SetPrincipalPepStatus(
            pep.Value, context.Clock.GetUtcNow(), scope.PepPostMandateMonths);

        if (set.IsFailure)
            return set;

        await context.CommitAsync(scope.Lead, ct);
        return Result.Ok();
    }
}

internal sealed class SetHouseholdMemberPepStatusHandler(HouseholdWriteContext context)
    : IRequestHandler<SetHouseholdMemberPepStatusCommand, Result>
{
    public async Task<Result> Handle(SetHouseholdMemberPepStatusCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.LeadId, cmd.ExpectedUpdatedAt, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var scope = loaded.Value;

        var pep = HouseholdMapping.ToPepStatus(cmd.Pep);
        if (pep.IsFailure)
            return Result.Fail(pep.Error!);

        var set = scope.Lead.SetHouseholdMemberPepStatus(
            cmd.MemberId, pep.Value, context.Clock.GetUtcNow(), scope.PepPostMandateMonths);

        if (set.IsFailure)
            return set;

        await context.CommitAsync(scope.Lead, ct);
        return Result.Ok();
    }
}
