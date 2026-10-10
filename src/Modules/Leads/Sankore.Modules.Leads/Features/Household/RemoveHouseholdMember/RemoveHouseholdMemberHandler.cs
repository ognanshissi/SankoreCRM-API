namespace Sankore.Modules.Leads.Features.Household.RemoveHouseholdMember;

using MediatR;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel;

internal sealed class RemoveHouseholdMemberHandler(HouseholdWriteContext context)
    : IRequestHandler<RemoveHouseholdMemberCommand, Result>
{
    public async Task<Result> Handle(RemoveHouseholdMemberCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.LeadId, cmd.ExpectedUpdatedAt, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var scope = loaded.Value;
        var now   = context.Clock.GetUtcNow();

        var removed = scope.Lead.RemoveHouseholdMember(cmd.MemberId, cmd.Reason, now);
        if (removed.IsFailure)
            return removed;

        // Removing the only exposed member can clear the lead's exposure — and that has to reach
        // dispatching, which routes an exposed lead differently.
        var recalculated = scope.Lead.RecalculateHasPepExposure(now, scope.PepPostMandateMonths);
        if (recalculated.IsFailure)
            return recalculated;

        await context.CommitAsync(scope.Lead, ct);
        return Result.Ok();
    }
}
