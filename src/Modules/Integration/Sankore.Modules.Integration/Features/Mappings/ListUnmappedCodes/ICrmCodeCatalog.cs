namespace Sankore.Modules.Integration.Features.Mappings.ListUnmappedCodes;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// The CRM's own code list for one mapping domain — the left-hand side of criterion 3, "list the
/// CRM codes with no mapping".
///
/// <para>
/// An interface, and not a direct call to <c>IAdministrationModule</c> inside the handler, for one
/// reason: most of the eight domains have NO enumerable code list behind any module contract
/// today (see <see cref="ContractCrmCodeCatalog"/>), so the part worth testing is the set
/// difference, and the part worth replacing one domain at a time is this.
/// </para>
/// </summary>
internal interface ICrmCodeCatalog
{
    Task<CrmCodeList> GetAsync(Guid tenantId, MappingDomain domain, CancellationToken ct);
}

/// <summary>
/// How much of a domain's code list could be obtained. The distinction matters more than it
/// looks: an operator reading an EMPTY "unmapped" list concludes everything is mapped, and that
/// conclusion is only valid for <see cref="Complete"/>.
/// </summary>
internal enum CrmCodeListAvailability
{
    /// <summary>The whole CRM list was obtained. An empty answer means nothing is missing.</summary>
    Complete,

    /// <summary>
    /// Only part of the list could be reached. An empty answer proves nothing — there may be
    /// unmapped codes the source cannot see.
    /// </summary>
    Partial,

    /// <summary>No module contract exposes this list. The answer is always empty.</summary>
    Unavailable
}

/// <param name="Source">
/// A sentence naming where the list came from, or why there is none. It is returned to the caller
/// verbatim, because "no unmapped codes" and "we cannot tell" look identical otherwise.
/// </param>
internal sealed record CrmCodeList(
    CrmCodeListAvailability Availability,
    string Source,
    IReadOnlyList<CrmCode> Codes)
{
    internal static CrmCodeList Unavailable(string source)
        => new(CrmCodeListAvailability.Unavailable, source, []);
}

/// <param name="Code">The value an administrator would type as the mapping's <c>crm_code</c>.</param>
internal sealed record CrmCode(string Code, string? Label);
