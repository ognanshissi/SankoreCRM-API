namespace Sankore.Modules.Customers.Features.Clients.UpdateClientSensitive;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// US-M01-BE-08 — edits the REGULATED part of a client's identity: legal names, identity
/// document and postal address. Separate from <c>UpdateClientCommand</c> because it needs
/// its own permission (<c>customers:update_sensitive</c>), a mandatory motive, and a
/// dedicated integration event so that AML / KYC consumers can re-open a file.
///
/// <see cref="Reason"/> is a business motive typed by the operator ("carte d'identité
/// renouvelée", "correction d'état civil"). It is stored in the audit trail and travels
/// on the event, so it must never contain a personal value — the validator caps it and
/// the UI labels it as a justification, not a free note.
///
/// The result is the list of field NAMES that actually changed — no value, before or
/// after. That is the whole point of the slice: the fact of the change is auditable, the
/// content stays behind the reveal endpoint.
/// </summary>
public sealed record UpdateClientSensitiveCommand(
    Guid ClientId,
    uint ExpectedVersion,
    string Reason,
    string? FirstName,
    string? LastName,
    string? MaidenName,
    IdentityDocumentType? IdentityDocumentType,
    [property: SensitiveData] string? IdentityDocumentNumber,
    DateOnly? IdentityDocumentIssuedOn,
    DateOnly? IdentityDocumentExpiresOn,
    [property: SensitiveData] PostalAddressInput? Address
) : IRequest<Result<IReadOnlyList<string>>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
