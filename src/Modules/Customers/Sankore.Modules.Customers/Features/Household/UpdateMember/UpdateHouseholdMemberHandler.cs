namespace Sankore.Modules.Customers.Features.Household.UpdateMember;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

internal sealed class UpdateHouseholdMemberHandler(
    HouseholdWriteContext context,
    IFieldEncryptor encryptor)
    : IRequestHandler<UpdateHouseholdMemberCommand, Result>
{
    public async Task<Result> Handle(UpdateHouseholdMemberCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.ClientId, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var scope = loaded.Value;

        var member = scope.Household.FirstOrDefault(m => m.Id == cmd.MemberId);
        if (member is null)
            return Result.Fail(CustomerErrors.RelationshipNotFound);

        if (cmd.SpouseUnionType is not null)
        {
            var unionType = HouseholdMapping.ToUnionType(cmd.SpouseUnionType);
            if (unionType.IsFailure)
                return Result.Fail(unionType.Error!);

            if (unionType.Value is { } value)
            {
                var updated = member.UpdateUnionType(value);
                if (updated.IsFailure)
                    return updated;
            }
        }

        if (cmd.Identity is not null)
        {
            var identity = HouseholdMapping.ToIdentity(cmd.Identity);
            if (identity.IsFailure)
                return Result.Fail(identity.Error!);

            var updated = member.UpdateHouseholdIdentity(
                identity.Value, cmd.UsesPrincipalAddress, plain => encryptor.Encrypt(plain)!);

            if (updated.IsFailure)
                return updated;
        }

        // Committed through the context even though no exposure can have changed here: the
        // recalculation is idempotent and cheap, and a second save path would be one more place
        // that could forget it.
        await context.CommitAsync(scope, ct);
        return Result.Ok();
    }
}
