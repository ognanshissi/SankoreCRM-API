namespace Sankore.Modules.Customers.Features.Holdings.Designate;

using MediatR;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Replaces a holding's beneficiary clause, as ONE atomic set (BNF-B-15).
///
/// <para>
/// A whole-clause replacement and not an add/remove pair, deliberately: the invariants are about
/// the set — a role's cardinality, and the shares of a rank totalling 100% — so an add-one endpoint
/// would either refuse every intermediate state (making a three-beneficiary clause impossible to
/// enter) or accept an invalid one.
/// </para>
///
/// <para>
/// On a holding that is already <c>Active</c> this writes a NEW VERSION and closes the previous
/// one: an avenant, not an edit. The response carries the resulting version, because that is what a
/// back office and a reconciliation run compare.
/// </para>
/// </summary>
public sealed record DesignateCommand(
    Guid HoldingId,
    IReadOnlyList<DesignationInput> Designations
) : IRequest<Result<DesignateResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductHolding";
    public string? ResourceId => HoldingId.ToString();
}

/// <param name="IsAmendment">
/// True when the holding was live and this produced a new version. A screen says "avenant
/// enregistré" rather than "clause enregistrée", and the agent knows the back office will be told.
/// </param>
public sealed record DesignateResult(int ClauseVersion, int DesignationCount, bool IsAmendment);
