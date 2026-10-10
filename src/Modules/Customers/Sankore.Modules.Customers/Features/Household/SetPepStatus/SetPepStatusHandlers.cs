namespace Sankore.Modules.Customers.Features.Household.SetPepStatus;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed class SetClientPepStatusHandler(HouseholdWriteContext context)
    : IRequestHandler<SetClientPepStatusCommand, Result>
{
    public async Task<Result> Handle(SetClientPepStatusCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.ClientId, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var scope = loaded.Value;

        var pep = HouseholdMapping.ToPepStatus(cmd.Pep);
        if (pep.IsFailure)
            return Result.Fail(pep.Error!);

        var declared = scope.Client.DeclarePepStatus(pep.Value, context.Clock.GetUtcNow());
        if (declared.IsFailure)
            return declared;

        // ── Propagation (BNF-B-13, mirroring BNF-B-05) ───────────────────────
        //
        // A client exposed as Self makes their household exposed as FamilyMember — except a member
        // an agent has already classified individually, which carries an OverrideReason.
        // Overwriting that would discard a judgement the story explicitly grants them.
        if (pep.Value is { IsPep: true, Link: PepLink.Self })
        {
            var now = context.Clock.GetUtcNow();

            foreach (var member in scope.Household
                         .Where(m => m.IsActive && m.Pep.OverrideReason is null))
            {
                member.SetPepStatus(pep.Value.AsFamilyMemberLink(), now);
            }
        }

        await context.CommitAsync(scope, ct);
        return Result.Ok();
    }
}

internal sealed class SetHouseholdMemberPepStatusHandler(HouseholdWriteContext context)
    : IRequestHandler<SetHouseholdMemberPepStatusCommand, Result>
{
    public async Task<Result> Handle(SetHouseholdMemberPepStatusCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.ClientId, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var scope = loaded.Value;

        var member = scope.Household.FirstOrDefault(m => m.Id == cmd.MemberId);
        if (member is null)
            return Result.Fail(CustomerErrors.RelationshipNotFound);

        var pep = HouseholdMapping.ToPepStatus(cmd.Pep);
        if (pep.IsFailure)
            return Result.Fail(pep.Error!);

        var set = member.SetPepStatus(pep.Value, context.Clock.GetUtcNow());
        if (set.IsFailure)
            return set;

        await context.CommitAsync(scope, ct);
        return Result.Ok();
    }
}
