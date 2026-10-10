namespace Sankore.Modules.Leads.Features.Household;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Maps between the household DTOs and the Kernel value objects — one place, so the HTTP shape and
/// the domain shape cannot drift apart across five handlers.
/// </summary>
internal static class HouseholdMapping
{
    internal static Result<PersonIdentity> ToIdentity(PersonIdentityDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (!TryParseEnum<PersonGender>(dto.Gender, PersonGender.Unknown, out var gender))
            return Result.Fail<PersonIdentity>("INVALID_GENDER");

        try
        {
            return Result.Ok(PersonIdentity.Create(
                lastName:    dto.LastName,
                firstName:   dto.FirstName,
                usageName:   dto.UsageName,
                birthDate:   dto.BirthDate,
                gender:      gender ?? PersonGender.Unknown,
                addressLine: dto.AddressLine,
                postalCode:  dto.PostalCode,
                city:        dto.City,
                countryCode: dto.CountryCode));
        }
        catch (DomainException ex)
        {
            // The factory throws on a blank name or a malformed country code. Turned into a result
            // so the handler answers 400 with a code rather than letting the exception handler
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

    internal static Result<HouseholdMemberType> ToMemberType(string? raw)
        => TryParseEnum<HouseholdMemberType>(raw, null, out var type) && type is not null
            ? Result.Ok(type.Value)
            : Result.Fail<HouseholdMemberType>("INVALID_HOUSEHOLD_MEMBER_TYPE");

    internal static Result<SpouseUnionType?> ToUnionType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result.Ok<SpouseUnionType?>(null);

        return TryParseEnum<SpouseUnionType>(raw, null, out var union) && union is not null
            ? Result.Ok<SpouseUnionType?>(union.Value)
            : Result.Fail<SpouseUnionType?>("INVALID_SPOUSE_UNION_TYPE");
    }

    /// <summary>
    /// Parses case-INSENSITIVELY but refuses a numeric string.
    ///
    /// <para>
    /// <c>Enum.TryParse</c> happily accepts "7" for any enum and yields an undefined value, so a
    /// client sending a number would store a member type that no <c>switch</c> covers. Checked
    /// with <c>IsDefined</c> rather than trusted.
    /// </para>
    /// </summary>
    private static bool TryParseEnum<T>(string? raw, T? fallback, out T? value) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            return fallback is not null;
        }

        if (Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }
}
