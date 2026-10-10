namespace Sankore.Modules.Customers.Features.Household.GetHousehold;

using MediatR;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// A client's household (BNF-B-13).
/// </summary>
/// <param name="IncludePepDetail">
/// Decided by the ENDPOINT from the caller's claims, never by the handler — so an authorization
/// reviewer finds the gate where they look for it. False masks the function, the country and the
/// family link; the exposure FLAG stays visible either way, because a designation screen has to
/// know without showing anybody why.
/// </param>
/// <param name="IncludeRemoved">
/// Removed members are kept and flagged, never dropped (BNF-B-13). Off by default: the question a
/// household screen asks is who is in it now.
/// </param>
public sealed record GetHouseholdQuery(
    Guid ClientId,
    bool IncludePepDetail,
    bool IncludeRemoved) : IRequest<Result<ClientHouseholdDto>>;
