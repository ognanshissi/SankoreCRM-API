namespace Sankore.Modules.Customers.Features.LegalEntities.ListBeneficialOwners;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// US-M01-BE-18 read side. A query: it does NOT implement <c>ICommand</c>, so it skips
/// the transaction and audit behaviors. It reveals nothing in clear either — the external
/// identity document comes back masked.
/// </summary>
public sealed record ListBeneficialOwnersQuery(
    Guid ClientId,
    bool IncludeClosed = false) : IRequest<Result<IReadOnlyList<BeneficialOwnerDto>>>;

/// <param name="ExternalDocumentNumberMasked">
/// Masked rendering of the encrypted document number (e.g. <c>"CI•••••••42"</c>).
/// The clear value is only ever served by the audited reveal endpoint.
/// </param>
/// <param name="ExternalDateOfBirthMasked">
/// Masked rendering (<c>"••/••/1987"</c>): a date of birth identifies a natural person,
/// so it follows the same read posture as the document number.
/// </param>
public sealed record BeneficialOwnerDto(
    Guid Id,
    Guid? LinkedClientId,
    string? LinkedClientNumber,
    string? LinkedClientDisplayName,
    string? ExternalFullName,
    string? ExternalNationality,
    string? ExternalDateOfBirthMasked,
    string? ExternalDocumentNumberMasked,
    decimal OwnershipPercentage,
    string ControlType,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    bool IsActive);
