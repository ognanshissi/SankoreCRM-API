namespace Sankore.Modules.Leads.Features.Household.UpdateHouseholdMember;

using MediatR;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Replaces a household member's identity, union type and address inheritance (BNF-B-03). The PEP
/// status is NOT here — it moves through its own command, because changing a classification is an
/// LBC/FT act that carries its own reason and its own exposure recalculation.
/// </summary>
internal sealed record UpdateHouseholdMemberCommand(
    Guid LeadId,
    Guid MemberId,
    PersonIdentityDto Identity,
    string? SpouseUnionType = null,
    bool UsesPrincipalAddress = true,
    DateTimeOffset? ExpectedUpdatedAt = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
