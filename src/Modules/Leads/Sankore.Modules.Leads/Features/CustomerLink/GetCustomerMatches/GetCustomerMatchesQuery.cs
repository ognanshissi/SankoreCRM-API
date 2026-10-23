namespace Sankore.Modules.Leads.Features.CustomerLink.GetCustomerMatches;

using MediatR;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Shared.Kernel;

/// <summary>
/// The clients a lead might belong to (BNF-B-22) — what the agent confirming or rejecting a
/// suggestion looks at. A query: no transaction, no audit row.
/// </summary>
internal sealed record GetCustomerMatchesQuery(Guid LeadId)
    : ILeadReadRequest, IRequest<Result<IReadOnlyList<CustomerMatchDto>>>;

/// <param name="MatchedOn">"phone" | "email" | "identityDocument" — several when several matched.</param>
/// <param name="IsExactSingleMatch">
/// True only when this is the ONE match found and it would have been attached automatically. It is
/// here so the screen can distinguish "we are confident" from "pick one", which is the whole
/// difference BNF-B-22 draws between an automatic link and a suggestion.
/// </param>
/// <param name="AlreadyLinked">True when the lead is already attached to this client.</param>
public sealed record CustomerMatchDto(
    Guid CustomerId,
    string ClientNumber,
    string DisplayName,
    Guid AgencyId,
    Guid? AdvisorUserId,
    string Status,
    string KycStatus,
    IReadOnlyList<string> MatchedOn,
    bool IsExactSingleMatch,
    bool AlreadyLinked);
