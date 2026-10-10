namespace Sankore.Modules.Administration.Features.Products.SetBeneficiaryPolicy;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Configures what a product line asks of the household and of the people designated on it
/// (BNF-B-06).
///
/// <para>
/// A full replacement, not a patch — the fields constrain each other, and a PATCH able to carry one
/// of them would let a line end up with a child-age ceiling and no household, or a share rule
/// pointing at a role that was removed in the same screen. <c>ICommand</c> brings the transaction
/// and the audit row BNF-B-06 requires; <c>IResourceCommand</c> names the product in it.
/// </para>
/// </summary>
/// <param name="AllowMultipleHoldings">
/// Whether a customer may hold this line more than once (BNF-B-22). Part of this command because
/// it is part of the same screen and the same decision, even though it is stored as a column
/// rather than inside the policy.
/// </param>
internal sealed record SetBeneficiaryPolicyCommand(
    Guid ProductId,
    bool HouseholdEnabled,
    bool AllowMultipleHoldings,
    IReadOnlyList<DesignationRoleInput>? AllowedRoles = null,
    int? MaxChildren = null,
    int? ChildMaxAge = null,
    IReadOnlyList<string>? RequiredFieldsBeforeConversion = null,
    string? RequiredKycTier = null,
    string? IntegrationSystem = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductSpeciality";
    public string? ResourceId => ProductId.ToString();
}

/// <param name="SharesMustTotal100">
/// Whether the quote-parts of this role must sum to 100% within a rank (BNF-B-15).
/// </param>
public sealed record DesignationRoleInput(
    string Code,
    string Label,
    int MinCount = 0,
    int? MaxCount = null,
    bool SharesMustTotal100 = false);
