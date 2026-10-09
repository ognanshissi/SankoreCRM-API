namespace Sankore.Modules.Integration.Features.Mappings.ListUnmappedCodes;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Kernel;

/// <summary>Criterion 3 of INT-04: the CRM codes of one domain that have no mapping yet.</summary>
internal sealed record ListUnmappedCodesQuery(Guid ConnectionId, MappingDomain Domain)
    : IRequest<Result<UnmappedCodesResponse>>;

/// <param name="Source">
/// Where the CRM list came from, or why there is none, in one sentence. Returned verbatim: it is
/// the ONLY thing distinguishing "nothing is missing" from "this cannot be checked", and the two
/// have opposite consequences for whoever is about to turn the connection on.
/// </param>
/// <param name="Availability">
/// <c>Complete</c>, <c>Partial</c> or <c>Unavailable</c>. An empty <paramref name="Codes"/> only
/// means "fully mapped" when this is <c>Complete</c>.
/// </param>
/// <param name="KnownCrmCodes">Size of the CRM list that could be obtained.</param>
/// <param name="MappedCodes">Existing mappings for this connection and domain.</param>
public sealed record UnmappedCodesResponse(
    string Domain,
    string Availability,
    string Source,
    int KnownCrmCodes,
    int MappedCodes,
    IReadOnlyList<UnmappedCrmCode> Codes);

public sealed record UnmappedCrmCode(string CrmCode, string? Label);
