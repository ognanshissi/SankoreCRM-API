namespace Sankore.Modules.Integration.Features.Mappings;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// Parsing of the <c>{domain}</c> route segment, shared by every slice of the area.
///
/// <para>
/// The segment is a NAME, never the enum's numeric value. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
/// happily accepts "3" and answers <c>Country</c>, which would let a caller address a domain by
/// an ordinal that changes the day a value is inserted in the middle of
/// <see cref="MappingDomain"/>. The digit guard below is what keeps the URL stable.
/// </para>
/// </summary>
internal static class MappingDomainRoute
{
    /// <summary>
    /// The eight domains, for the OpenAPI descriptions. Written out rather than derived from
    /// <c>Enum.GetNames</c> so the order shown to an operator is the specification's order.
    /// </summary>
    internal const string Values =
        "IdDocType, Product, Agency, Country, Gender, MaritalStatus, Profession, Sector";

    internal static bool TryParse(string? value, out MappingDomain domain)
    {
        domain = default;

        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();

        // Reject anything that starts like a number: see the class remark.
        if (!char.IsLetter(trimmed[0])) return false;

        return Enum.TryParse(trimmed, ignoreCase: true, out domain) && Enum.IsDefined(domain);
    }
}
