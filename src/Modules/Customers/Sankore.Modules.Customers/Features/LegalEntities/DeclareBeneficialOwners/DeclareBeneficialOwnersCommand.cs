namespace Sankore.Modules.Customers.Features.LegalEntities.DeclareBeneficialOwners;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// US-M01-BE-18 — declares the COMPLETE beneficial-owner structure of a legal client.
/// <para>
/// The declaration is SET-BASED, not incremental: active owners missing from
/// <paramref name="Owners"/> are closed by <c>ValidTo</c> (never deleted, the AML trail
/// must stay auditable) and owners not yet present are created.
/// </para>
/// </summary>
public sealed record DeclareBeneficialOwnersCommand(
    Guid ClientId,
    IReadOnlyList<BeneficialOwnerInput> Owners,
    string Reason
) : IRequest<Result<DeclareBeneficialOwnersResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>
/// One declared beneficial owner. Exactly one of <paramref name="LinkedClientId"/> (an
/// existing client of the base) or <paramref name="ExternalFullName"/> (a third party) is set.
/// </summary>
public sealed record BeneficialOwnerInput(
    Guid? LinkedClientId,
    string? ExternalFullName,
    string? ExternalNationality,
    DateOnly? ExternalDateOfBirth,
    [property: SensitiveData] string? ExternalDocumentNumber,
    decimal OwnershipPercentage,
    ControlType ControlType);

/// <param name="ActiveOwnerCount">Owners active after the declaration.</param>
/// <param name="Created">Rows opened by this declaration.</param>
/// <param name="Closed">Previously active rows closed by this declaration.</param>
public sealed record DeclareBeneficialOwnersResult(
    int ActiveOwnerCount,
    int Created,
    int Closed);
