namespace Sankore.Modules.Customers.Features.Household.UpdateMember;

using MediatR;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Corrects a household member's own identity, or a spouse's union type (BNF-B-13).
///
/// <para>
/// A WHOLE identity, not a patch: <c>PersonIdentity</c> requires both names and normalises every
/// other field, so a partial update would have to reconstruct the value object from the stored
/// columns and would silently keep a field the agent meant to clear. The PEP status has its own
/// command, because declaring one is a compliance act with its own audit meaning.
/// </para>
/// </summary>
public sealed record UpdateHouseholdMemberCommand(
    Guid ClientId,
    Guid MemberId,
    PersonIdentityDto? Identity,
    string? SpouseUnionType,
    bool UsesPrincipalAddress
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
