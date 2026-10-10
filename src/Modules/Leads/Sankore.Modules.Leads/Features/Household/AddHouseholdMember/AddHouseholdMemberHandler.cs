namespace Sankore.Modules.Leads.Features.Household.AddHouseholdMember;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed class AddHouseholdMemberHandler(HouseholdWriteContext context)
    : IRequestHandler<AddHouseholdMemberCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddHouseholdMemberCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.LeadId, cmd.ExpectedUpdatedAt, ct);
        if (loaded.IsFailure)
            return Result.Fail<Guid>(loaded.Error!);

        var scope = loaded.Value;
        var lead  = scope.Lead;

        var type = HouseholdMapping.ToMemberType(cmd.Type);
        if (type.IsFailure)
            return Result.Fail<Guid>(type.Error!);

        var union = HouseholdMapping.ToUnionType(cmd.SpouseUnionType);
        if (union.IsFailure)
            return Result.Fail<Guid>(union.Error!);

        var identity = HouseholdMapping.ToIdentity(cmd.Identity);
        if (identity.IsFailure)
            return Result.Fail<Guid>(identity.Error!);

        // Null when the caller supplied nothing: the aggregate then applies BNF-B-05's default,
        // inheriting FamilyMember from a principal exposed as Self.
        Sankore.Shared.Kernel.ValueObject.PepStatus? pep = null;
        if (cmd.Pep is not null)
        {
            var parsed = HouseholdMapping.ToPepStatus(cmd.Pep);
            if (parsed.IsFailure)
                return Result.Fail<Guid>(parsed.Error!);
            pep = parsed.Value;
        }

        var added = lead.AddHouseholdMember(
            type:                 type.Value,
            spouseUnionType:      union.Value,
            identity:             identity.Value,
            pep:                  pep,
            usesPrincipalAddress: cmd.UsesPrincipalAddress,
            now:                  context.Clock.GetUtcNow(),
            maxChildren:          scope.Limits.MaxChildren,
            childMaxAge:          scope.Limits.ChildMaxAge);

        if (added.IsFailure)
            return Result.Fail<Guid>(added.Error!);

        // Adding an exposed member can flip the lead's exposure, and the aggregate only
        // recalculates it from the PEP mutators — so it is asked explicitly here.
        var recalculated = lead.RecalculateHasPepExposure(
            context.Clock.GetUtcNow(), scope.PepPostMandateMonths);
        if (recalculated.IsFailure)
            return Result.Fail<Guid>(recalculated.Error!);

        await context.CommitAsync(lead, ct);
        return Result.Ok(added.Value.Id);
    }
}
