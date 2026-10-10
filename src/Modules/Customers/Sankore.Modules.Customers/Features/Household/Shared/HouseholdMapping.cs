namespace Sankore.Modules.Customers.Features.Household.Shared;

using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Translation between the household DTOs and the shared value objects (BNF-B-13).
/// </summary>
/// <remarks>
/// It mirrors M13's <c>HouseholdMapping</c> deliberately rather than being shared with it: the two
/// modules may not reference each other, and the alternative — putting a DTO mapper in
/// Shared.Infrastructure — would make a wire contract of one module a dependency of every other.
/// The VALUE OBJECTS are shared, which is what actually has to agree.
/// </remarks>
internal static class HouseholdMapping
{
    internal static Result<PersonIdentity> ToIdentity(PersonIdentityDto? dto)
    {
        if (dto is null)
            return Result.Fail<PersonIdentity>("IDENTITY_REQUIRED");

        if (!TryParseEnum<PersonGender>(dto.Gender, PersonGender.Unknown, out var gender))
            return Result.Fail<PersonIdentity>("INVALID_GENDER");

        try
        {
            return Result.Ok(PersonIdentity.Create(
                lastName:    dto.LastName,
                firstName:   dto.FirstName,
                usageName:   dto.UsageName,
                birthDate:   dto.BirthDate,
                gender:      gender!.Value,
                addressLine: dto.AddressLine,
                postalCode:  dto.PostalCode,
                city:        dto.City,
                countryCode: dto.CountryCode));
        }
        catch (DomainException ex)
        {
            // The factory throws on a blank name or a malformed country code. Turned into a result
            // so the endpoint answers 400 with a code rather than letting the exception handler
            // render a domain invariant as a server fault.
            return Result.Fail<PersonIdentity>(ex.MessageKey ?? "INVALID_IDENTITY");
        }
    }

    internal static Result<PepStatus> ToPepStatus(PepStatusDto? dto)
    {
        if (dto is null || !dto.IsPep)
            return Result.Ok(PepStatus.NotExposed);

        if (!TryParseEnum<PepLink>(dto.Link, null, out var link) || link is null)
            return Result.Fail<PepStatus>("PEP_LINK_REQUIRED");

        try
        {
            return Result.Ok(PepStatus.Exposed(
                function:        dto.Function ?? string.Empty,
                link:            link.Value,
                countryCode:     dto.CountryCode,
                functionEndDate: dto.FunctionEndDate,
                overrideReason:  dto.OverrideReason));
        }
        catch (DomainException ex)
        {
            return Result.Fail<PepStatus>(ex.MessageKey ?? "INVALID_PEP_STATUS");
        }
    }

    internal static Result<RelationshipType> ToHouseholdType(string? raw)
    {
        if (!TryParseEnum<RelationshipType>(raw, null, out var type) || type is null)
            return Result.Fail<RelationshipType>("INVALID_HOUSEHOLD_MEMBER_TYPE");

        // Only the three the household is made of. A guarantor or a proxy is a relationship and
        // goes through the relationships endpoints — admitting them here would make the one-spouse
        // invariant and the household read mean something different on each screen.
        return type is RelationshipType.Spouse or RelationshipType.Child or RelationshipType.Dependent
            ? Result.Ok(type.Value)
            : Result.Fail<RelationshipType>(CustomerErrors.NotAHouseholdMember);
    }

    internal static Result<SpouseUnionType?> ToUnionType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result.Ok<SpouseUnionType?>(null);

        return TryParseEnum<SpouseUnionType>(raw, null, out var union) && union is not null
            ? Result.Ok<SpouseUnionType?>(union.Value)
            : Result.Fail<SpouseUnionType?>("INVALID_SPOUSE_UNION_TYPE");
    }

    internal static PersonIdentityDto ToDto(PersonIdentity identity) => new(
        LastName:    identity.LastName,
        FirstName:   identity.FirstName,
        UsageName:   identity.UsageName,
        BirthDate:   identity.BirthDate,
        Gender:      identity.Gender.ToString(),
        AddressLine: identity.AddressLine,
        PostalCode:  identity.PostalCode,
        City:        identity.City,
        CountryCode: identity.CountryCode);

    internal static PepStatusDto ToDto(PepStatus pep) => new(
        IsPep:           pep.IsPep,
        Function:        pep.Function,
        CountryCode:     pep.CountryCode,
        FunctionEndDate: pep.FunctionEndDate,
        Link:            pep.Link?.ToString(),
        OverrideReason:  pep.OverrideReason);

    /// <summary>
    /// Parses an enum name, treating blank as <paramref name="whenBlank"/>. Case-insensitive,
    /// because the wire carries whatever a front-end sent.
    /// </summary>
    private static bool TryParseEnum<T>(string? raw, T? whenBlank, out T? value)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = whenBlank;
            return whenBlank is not null;
        }

        if (Enum.TryParse<T>(raw, ignoreCase: true, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }
}
