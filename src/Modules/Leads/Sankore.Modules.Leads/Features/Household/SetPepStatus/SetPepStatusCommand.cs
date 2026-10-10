namespace Sankore.Modules.Leads.Features.Household.SetPepStatus;

using MediatR;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Declares the PEP status of the PRINCIPAL (BNF-B-05).
///
/// <para>
/// Declaring a <c>Self</c> exposure propagates <c>FamilyMember</c> onto every active household
/// member nobody has classified individually — which is why this is its own command rather than a
/// field of the enrichment: one request changes several people's LBC/FT classification, and the
/// audit row has to say so.
/// </para>
/// </summary>
internal sealed record SetPrincipalPepStatusCommand(
    Guid LeadId,
    PepStatusDto Pep,
    DateTimeOffset? ExpectedUpdatedAt = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

/// <summary>
/// Declares the PEP status of one household member (BNF-B-05) — the "modifiable avec motif" half
/// of the default propagation.
/// </summary>
internal sealed record SetHouseholdMemberPepStatusCommand(
    Guid LeadId,
    Guid MemberId,
    PepStatusDto Pep,
    DateTimeOffset? ExpectedUpdatedAt = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
