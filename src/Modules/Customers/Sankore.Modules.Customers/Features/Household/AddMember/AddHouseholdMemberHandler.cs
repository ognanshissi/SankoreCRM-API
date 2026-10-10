namespace Sankore.Modules.Customers.Features.Household.AddMember;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed class AddHouseholdMemberHandler(
    CustomersDbContext db,
    HouseholdWriteContext context,
    ICurrentUser currentUser,
    IFieldEncryptor encryptor)
    : IRequestHandler<AddHouseholdMemberCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddHouseholdMemberCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.ClientId, ct);
        if (loaded.IsFailure)
            return Result.Fail<Guid>(loaded.Error!);

        var scope = loaded.Value;

        var type = HouseholdMapping.ToHouseholdType(cmd.Type);
        if (type.IsFailure)
            return Result.Fail<Guid>(type.Error!);

        var unionType = HouseholdMapping.ToUnionType(cmd.SpouseUnionType);
        if (unionType.IsFailure)
            return Result.Fail<Guid>(unionType.Error!);

        var pep = HouseholdMapping.ToPepStatus(cmd.Pep);
        if (pep.IsFailure)
            return Result.Fail<Guid>(pep.Error!);

        // At most one ACTIVE spouse. Checked here for an actionable code, and held by
        // uq_client_one_spouse for the two requests that arrive at the same instant — which no
        // in-memory check can cover.
        if (type.Value == RelationshipType.Spouse
            && scope.Household.Any(m => m.Type == RelationshipType.Spouse && m.IsActive))
            return Result.Fail<Guid>(CustomerErrors.HouseholdAlreadyHasSpouse);

        PersonIdentity identity;

        if (cmd.RelatedClientId is { } relatedId && relatedId != Guid.Empty)
        {
            var resolved = await ResolveClientIdentityAsync(relatedId, ct);
            if (resolved.IsFailure)
                return Result.Fail<Guid>(resolved.Error!);

            identity = resolved.Value;
        }
        else
        {
            var mapped = HouseholdMapping.ToIdentity(cmd.Identity);
            if (mapped.IsFailure)
                return Result.Fail<Guid>(mapped.Error!);

            identity = mapped.Value;
        }

        ClientRelationship member;
        try
        {
            member = ClientRelationship.HouseholdMember(
                tenantId:             scope.Client.TenantId,
                clientId:             cmd.ClientId,
                type:                 type.Value,
                identity:             identity,
                spouseUnionType:      unionType.Value,
                // Null when the caller sent nothing, so the member reads as "never asked" rather
                // than as a declared negative — see ClientRelationship.PepDeclaredAt.
                pep:                  cmd.Pep is null ? null : pep.Value,
                usesPrincipalAddress: cmd.UsesPrincipalAddress,
                // Wrapped rather than passed directly: IFieldEncryptor.Encrypt takes and returns
                // nullable, and the aggregate only ever calls it with a value it has already
                // checked. The `!` is the assertion the signature cannot make.
                encryptAddress:       plain => encryptor.Encrypt(plain)!,
                validFrom:            context.Clock.GetUtcNow(),
                createdBy:            currentUser.Id,
                relatedClientId:      cmd.RelatedClientId == Guid.Empty ? null : cmd.RelatedClientId,
                sourceLeadMemberId:   cmd.SourceLeadMemberId);
        }
        catch (DomainException ex)
        {
            // The factory enforces the type/union-type pairing and throws. Reported as a code
            // rather than a 500, the same treatment the identity mapper gives.
            return Result.Fail<Guid>(ex.MessageKey switch
            {
                "ClientRelationship.Household.UnionTypeRequired"  => CustomerErrors.UnionTypeRequired,
                "ClientRelationship.Household.UnionTypeForbidden" => CustomerErrors.UnionTypeForbidden,
                _                                                 => CustomerErrors.NotAHouseholdMember,
            });
        }

        db.ClientRelationships.Add(member);

        // Into the tracked list BEFORE the commit, so the exposure recalculation sees the member
        // this request just added. Re-querying would not: the row is not in the database yet.
        scope.Household.Add(member);

        // Recomputed from the active rows rather than incremented, so a replay or a closed
        // relationship can never drift the count — the rule AddRelationshipHandler established.
        var dependents = scope.Household.Count(m =>
            m.IsActive && m.Type is RelationshipType.Dependent or RelationshipType.Child);

        scope.Client.RecomputeDependentsCount(dependents);

        await context.CommitAsync(scope, ct);
        return Result.Ok(member.Id);
    }

    /// <summary>
    /// A member who is a client has their identity on that record. Read once here to validate the
    /// link and to get a name for the household screen — never copied onto the relationship row.
    /// </summary>
    private async Task<Result<PersonIdentity>> ResolveClientIdentityAsync(
        Guid relatedClientId, CancellationToken ct)
    {
        if (relatedClientId == Guid.Empty)
            return Result.Fail<PersonIdentity>(CustomerErrors.ClientNotFound);

        var related = await db.Clients
            .Where(c => c.Id == relatedClientId)
            .Select(c => new { c.FirstName, c.LastName, c.DisplayName, c.Status })
            .FirstOrDefaultAsync(ct);

        if (related is null)
            return Result.Fail<PersonIdentity>(CustomerErrors.ClientNotFound);

        if (related.Status is ClientStatus.Archived or ClientStatus.Merged)
            return Result.Fail<PersonIdentity>(CustomerErrors.ClientReadOnly);

        // A legal entity has no first and last name, and a household is made of natural persons.
        // The factory would throw on the blank name; refusing with a code says why.
        if (string.IsNullOrWhiteSpace(related.FirstName) || string.IsNullOrWhiteSpace(related.LastName))
            return Result.Fail<PersonIdentity>(CustomerErrors.NotAHouseholdMember);

        return Result.Ok(PersonIdentity.Create(related.LastName, related.FirstName));
    }
}
