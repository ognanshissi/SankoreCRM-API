namespace Sankore.Modules.Customers.Features.Household.SetPepStatus;

using MediatR;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Declares the CLIENT's own PEP status (BNF-B-13).
///
/// <para>
/// Its own command and not a field of an update, for the reason M13 established: this is a
/// compliance act, and the audit row has to say that somebody declared a political exposure rather
/// than that somebody edited a client.
/// </para>
/// </summary>
public sealed record SetClientPepStatusCommand(
    Guid ClientId,
    PepStatusDto Pep
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>Declares one household member's PEP status (BNF-B-13).</summary>
public sealed record SetHouseholdMemberPepStatusCommand(
    Guid ClientId,
    Guid MemberId,
    PepStatusDto Pep
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
