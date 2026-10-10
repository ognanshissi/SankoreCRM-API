namespace Sankore.Modules.Leads.Features.Household;

/// <summary>
/// A person's identity as the household endpoints accept and return it (BNF-B-02).
///
/// <para>
/// Flat and string-typed rather than the Kernel's <c>PersonIdentity</c>: this is the HTTP contract
/// the Angular client is generated from, and a record with a private constructor and a factory
/// does not round-trip through <c>System.Text.Json</c>. The handler maps it, which is also where
/// the validation failure becomes a 400 instead of an exception.
/// </para>
/// </summary>
public sealed record PersonIdentityDto(
    string LastName,
    string FirstName,
    string? UsageName = null,
    DateOnly? BirthDate = null,
    /// <summary>"Unknown" | "Male" | "Female" | "Other".</summary>
    string? Gender = null,
    string? AddressLine = null,
    string? PostalCode = null,
    string? City = null,
    /// <summary>ISO 3166-1 alpha-2.</summary>
    string? CountryCode = null);

/// <summary>
/// A PEP declaration as the endpoints accept and return it (BNF-B-05).
/// </summary>
/// <param name="Link">"Self" | "FamilyMember" | "CloseAssociate". Required when exposed.</param>
/// <param name="OverrideReason">
/// Why this classification departs from what the principal's status implies. Required to override
/// the <c>FamilyMember</c> link BNF-B-05 propagates onto the household by default.
/// </param>
public sealed record PepStatusDto(
    bool IsPep,
    string? Function = null,
    string? CountryCode = null,
    DateOnly? FunctionEndDate = null,
    string? Link = null,
    string? OverrideReason = null);

/// <summary>
/// One household member as returned by <c>GET leads/{id}/household</c>.
/// </summary>
/// <param name="Identity">
/// RESOLVED: a member inheriting the principal's address carries it here, not the empty fields its
/// own row holds (BNF-B-03).
/// </param>
/// <param name="Pep">
/// Null unless the caller holds <c>lead:pep:read</c>. The exposure FLAG is in
/// <paramref name="IsPepExposed"/> and stays visible either way — dispatching has to route an
/// exposed lead without showing anybody why.
/// </param>
/// <param name="Age">Whole years at the time of the read, or null when the birth date is unknown.</param>
public sealed record HouseholdMemberDto(
    Guid MemberId,
    string Type,
    string? SpouseUnionType,
    bool UsesPrincipalAddress,
    PersonIdentityDto Identity,
    int? Age,
    bool IsPepExposed,
    PepStatusDto? Pep,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RemovedAt,
    string? RemovalReason);

/// <summary>
/// The household of a lead (BNF-B-03, BNF-B-05).
/// </summary>
/// <param name="HouseholdEnabled">
/// False when the product line records no household. The screen needs it to explain why the add
/// buttons are absent, rather than letting the agent discover it on a refused write.
/// </param>
/// <param name="Members">Active members first; removed ones are kept and flagged, never dropped.</param>
public sealed record LeadHouseholdDto(
    Guid LeadId,
    bool HouseholdEnabled,
    int? MaxChildren,
    int? ChildMaxAge,
    PersonIdentityDto? Principal,
    bool PrincipalIsPepExposed,
    PepStatusDto? PrincipalPep,
    bool HasPepExposure,
    IReadOnlyList<HouseholdMemberDto> Members);
