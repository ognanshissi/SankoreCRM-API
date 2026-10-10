namespace Sankore.Modules.Customers.Features.Household.Shared;

using Sankore.Modules.Customers.Domain;

/// <summary>
/// A person as the household endpoints accept and return them (BNF-B-13). The same shape as M13's,
/// so a conversion snapshot and a client household read the same on screen.
/// </summary>
public sealed record PersonIdentityDto(
    string LastName,
    string FirstName,
    string? UsageName = null,
    DateOnly? BirthDate = null,
    /// <summary>A <c>PersonGender</c> name.</summary>
    string? Gender = null,
    string? AddressLine = null,
    string? PostalCode = null,
    string? City = null,
    /// <summary>ISO 3166-1 alpha-2.</summary>
    string? CountryCode = null);

/// <summary>
/// A PEP declaration (BNF-B-13).
/// </summary>
/// <param name="IsPep">
/// False is a real answer. Submitting <c>{ "isPep": false }</c> is how an agent records that they
/// asked and the answer was no — see <c>ClientRelationship.PepDeclaredAt</c>.
/// </param>
/// <param name="Link">"Self" | "FamilyMember" | "CloseAssociate". Required when exposed.</param>
public sealed record PepStatusDto(
    bool IsPep,
    string? Function = null,
    string? CountryCode = null,
    DateOnly? FunctionEndDate = null,
    string? Link = null,
    string? OverrideReason = null);

/// <summary>
/// One household member (BNF-B-13).
/// </summary>
/// <param name="Identity">
/// RESOLVED — a member inheriting the client's address carries it here, not the empty fields its
/// own row holds. Null when the member is a client in their own right: their identity is on that
/// record, and <paramref name="RelatedClientId"/> is where to read it.
/// </param>
/// <param name="Pep">
/// Null unless the caller holds <c>customers:pep_read</c>. The exposure FLAG stays visible either
/// way — a designation screen has to know without showing anybody why.
/// </param>
public sealed record HouseholdMemberDto(
    Guid MemberId,
    string Type,
    string? SpouseUnionType,
    bool UsesPrincipalAddress,
    Guid? RelatedClientId,
    string? DisplayName,
    PersonIdentityDto? Identity,
    int? Age,
    bool IsPepExposed,
    PepStatusDto? Pep,
    Guid? SourceLeadMemberId,
    bool IsActive,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    string? CloseReason);

/// <summary>
/// A client's household (BNF-B-13).
/// </summary>
/// <param name="MaxChildren">
/// Always null, and that is not an omission. A ceiling on children is a PRODUCT LINE rule
/// (<c>BeneficiaryPolicy.MaxChildren</c>), and a client holds several lines at once — so there is
/// no single ceiling to report here. The lead side has one because one lead is one request for one
/// line. The field is kept so the two screens share a shape, and documented so nobody fills it in
/// with the wrong answer.
/// </param>
/// <param name="CanEdit">
/// Whether a household write would currently be accepted (BNF-B-20). False for an archived or
/// merged client, which accepts no mutation at all — a server rule a screen would otherwise
/// re-derive from a status string, and get wrong the first time a status is added.
/// </param>
public sealed record ClientHouseholdDto(
    Guid ClientId,
    bool CanEdit,
    PersonIdentityDto? Principal,
    bool PrincipalIsPepExposed,
    PepStatusDto? PrincipalPep,
    bool HasPepExposure,
    int? MaxChildren,
    IReadOnlyList<HouseholdMemberDto> Members);
