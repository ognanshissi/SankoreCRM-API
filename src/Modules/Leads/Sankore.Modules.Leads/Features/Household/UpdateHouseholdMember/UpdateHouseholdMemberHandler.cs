namespace Sankore.Modules.Leads.Features.Household.UpdateHouseholdMember;

using MediatR;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel;

internal sealed class UpdateHouseholdMemberHandler(HouseholdWriteContext context)
    : IRequestHandler<UpdateHouseholdMemberCommand, Result>
{
    public async Task<Result> Handle(UpdateHouseholdMemberCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.LeadId, cmd.ExpectedUpdatedAt, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var scope = loaded.Value;

        var union = HouseholdMapping.ToUnionType(cmd.SpouseUnionType);
        if (union.IsFailure)
            return Result.Fail(union.Error!);

        var identity = HouseholdMapping.ToIdentity(cmd.Identity);
        if (identity.IsFailure)
            return Result.Fail(identity.Error!);

        var updated = scope.Lead.UpdateHouseholdMember(
            memberId:             cmd.MemberId,
            identity:             identity.Value,
            spouseUnionType:      union.Value,
            usesPrincipalAddress: cmd.UsesPrincipalAddress,
            now:                  context.Clock.GetUtcNow(),
            childMaxAge:          scope.Limits.ChildMaxAge);

        if (updated.IsFailure)
            return updated;

        await context.CommitAsync(scope.Lead, ct);
        return Result.Ok();
    }
}
