namespace Sankore.Modules.Customers.Features.Household.AddMember;

using MediatR;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Records a spouse, a child or a dependent on a client's household (BNF-B-13).
/// </summary>
/// <param name="Type">"Spouse" | "Child" | "Dependent". Anything else is refused.</param>
/// <param name="RelatedClientId">
/// Set when the member is a client of the tenant in their own right. Their identity then lives on
/// that record and <paramref name="Identity"/> is ignored — writing it here would create a second,
/// diverging copy, which is exactly what keeping the household on this table avoids.
/// </param>
/// <param name="UsesPrincipalAddress">
/// When true the member's postal fields are DROPPED and the address is resolved from the client at
/// read time. A link, not a copy.
/// </param>
/// <param name="SourceLeadMemberId">
/// The <c>LeadHouseholdMember</c> this came from, for a household arriving with a conversion.
/// Opaque; nothing validates it, which is why it is absent from the public capture contracts and
/// only ever set by a server-side caller.
/// </param>
public sealed record AddHouseholdMemberCommand(
    Guid ClientId,
    string Type,
    PersonIdentityDto? Identity,
    string? SpouseUnionType,
    PepStatusDto? Pep,
    bool UsesPrincipalAddress,
    Guid? RelatedClientId = null,
    Guid? SourceLeadMemberId = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
