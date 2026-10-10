namespace Sankore.Modules.Customers.Features.Household.GetHousehold;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Sankore.Shared.Kernel.ValueObject;

internal sealed class GetHouseholdHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IFieldEncryptor encryptor,
    TimeProvider clock)
    : IRequestHandler<GetHouseholdQuery, Result<ClientHouseholdDto>>
{
    public async Task<Result<ClientHouseholdDto>> Handle(
        GetHouseholdQuery query, CancellationToken ct)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == query.ClientId, ct);

        // Out of perimeter reads as missing, never 403 — the module's rule, so a response cannot
        // be used to probe for a client's existence.
        if (client is null ||
            !await agencyScope.CanAccessAgencyAsync(currentUser.TenantId, currentUser.Id, client.AgencyId, ct))
            return Result.Fail<ClientHouseholdDto>(CustomerErrors.ClientNotFound);

        var members = await db.ClientRelationships
            .Where(r => r.ClientId == query.ClientId
                     && (r.Type == RelationshipType.Spouse
                      || r.Type == RelationshipType.Child
                      || r.Type == RelationshipType.Dependent)
                     && (query.IncludeRemoved || r.ValidTo == null))
            .OrderBy(r => r.ValidTo == null ? 0 : 1)
            .ThenBy(r => r.Type)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(ct);

        // One batched read for the members who are clients in their own right. Their identity lives
        // on that record and is deliberately not copied onto the relationship, so the join is the
        // price of having one home per value.
        var relatedIds = members
            .Where(m => m.RelatedClientId is not null)
            .Select(m => m.RelatedClientId!.Value)
            .Distinct()
            .ToList();

        var relatedNames = relatedIds.Count == 0
            ? []
            : await db.Clients
                .Where(c => relatedIds.Contains(c.Id))
                .Select(c => new { c.Id, c.DisplayName })
                .ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);

        var principal = PrincipalIdentity(client);
        var asOf = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var dtos = members.Select(m =>
        {
            var identity = m.ResolveIdentity(principal, cipher => encryptor.Decrypt(cipher)!);

            return new HouseholdMemberDto(
                MemberId:             m.Id,
                Type:                 m.Type.ToString(),
                SpouseUnionType:      m.SpouseUnionType?.ToString(),
                UsesPrincipalAddress: m.UsesPrincipalAddress,
                RelatedClientId:      m.RelatedClientId,
                DisplayName:          m.RelatedClientId is { } id && relatedNames.TryGetValue(id, out var name)
                                          ? name
                                          : m.ExternalFullName,
                Identity:             identity is null ? null : HouseholdMapping.ToDto(identity),
                Age:                  identity?.AgeAt(asOf),
                // The FLAG, always. The DETAIL, only behind the permission.
                IsPepExposed:         m.Pep.IsPep,
                Pep:                  query.IncludePepDetail ? HouseholdMapping.ToDto(m.Pep) : null,
                SourceLeadMemberId:   m.SourceLeadMemberId,
                IsActive:             m.IsActive,
                ValidFrom:            m.ValidFrom,
                ValidTo:              m.ValidTo,
                CloseReason:          m.CloseReason);
        }).ToList();

        return Result.Ok(new ClientHouseholdDto(
            ClientId:              client.Id,
            // The aggregate's own rule, not a re-derivation from the status string (BNF-B-20).
            CanEdit:               !client.IsReadOnly,
            Principal:             principal is null ? null : HouseholdMapping.ToDto(principal),
            PrincipalIsPepExposed: client.PrincipalPep.IsPep,
            PrincipalPep:          query.IncludePepDetail
                                       ? HouseholdMapping.ToDto(client.PrincipalPep)
                                       : null,
            HasPepExposure:        client.HasPepExposure,
            // Always null — a child ceiling is a product-line rule and a client holds several
            // lines. See ClientHouseholdDto.MaxChildren.
            MaxChildren:           null,
            Members:               dtos));
    }

    /// <summary>
    /// The client as a <c>PersonIdentity</c>, for resolving a member's inherited address.
    /// </summary>
    /// <remarks>
    /// Null for a legal entity and for an individual whose structured names are missing, which is
    /// the same rule <c>Lead.PrincipalIdentity</c> follows — so a member that inherits an address
    /// the client does not have resolves to no address, and the caller must cope.
    ///
    /// <para>
    /// The client's own postal address is NOT read here. It lives on <c>ClientContactPoint</c> rows
    /// as encrypted values with their own reveal rules, and pulling one into an unaudited household
    /// read would route around them. A member that inherits the address therefore shows the city
    /// and country it stores and no street — which is what the household screen needs, and no more.
    /// </para>
    /// </remarks>
    private static PersonIdentity? PrincipalIdentity(Client client)
        => string.IsNullOrWhiteSpace(client.FirstName) || string.IsNullOrWhiteSpace(client.LastName)
            ? null
            : PersonIdentity.Create(
                lastName:  client.LastName,
                firstName: client.FirstName,
                gender: client.Gender switch
                {
                    Domain.Gender.Male   => PersonGender.Male,
                    Domain.Gender.Female => PersonGender.Female,
                    _                    => PersonGender.Unknown,
                });
}
