namespace Sankore.Modules.Leads.Features.Household.AddHouseholdMember;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Adds a spouse or a child to a lead's household (BNF-B-03).
/// </summary>
/// <param name="Type">"Spouse" | "Child".</param>
/// <param name="SpouseUnionType">"Married" | "CivilUnion". Required for a spouse, forbidden for a child.</param>
/// <param name="UsesPrincipalAddress">
/// Defaults to TRUE — BNF-B-03's default for a child, and the common case for a spouse too. When
/// true the member's own postal fields are cleared and the address is resolved from the principal
/// at read time; when false the identity must carry its own address.
/// </param>
/// <param name="Pep">
/// Omit to inherit: BNF-B-05 propagates <c>FamilyMember</c> onto the household of a principal
/// exposed as <c>Self</c>. Supply one to override, with a reason.
/// </param>
internal sealed record AddHouseholdMemberCommand(
    Guid LeadId,
    string Type,
    PersonIdentityDto Identity,
    string? SpouseUnionType = null,
    bool UsesPrincipalAddress = true,
    PepStatusDto? Pep = null,
    DateTimeOffset? ExpectedUpdatedAt = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
